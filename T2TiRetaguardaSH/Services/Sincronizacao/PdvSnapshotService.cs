using Microsoft.Extensions.Configuration;
using MySql.Data.MySqlClient;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using T2TiRetaguardaSH.Models.Sincronizacao;
using T2TiRetaguardaSH.Util;

namespace T2TiRetaguardaSH.Services.Sincronizacao
{
    public class PdvSnapshotService
    {
        private readonly string _adminConnectionString;
        private readonly string _retaguardaDatabase;
        private readonly string _operacionalDatabaseOverride;

        private static readonly HashSet<string> TabelasIgnoradas = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "__EFMIGRATIONSHISTORY",
            "SQLITE_SEQUENCE"
        };

        private static readonly Dictionary<string, string> EmpresaColunas = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["RazaoSocial"] = "RAZAO_SOCIAL",
            ["NomeFantasia"] = "NOME_FANTASIA",
            ["Cnpj"] = "CNPJ",
            ["InscricaoEstadual"] = "INSCRICAO_ESTADUAL",
            ["InscricaoMunicipal"] = "INSCRICAO_MUNICIPAL",
            ["TipoRegime"] = "TIPO_REGIME",
            ["Crt"] = "CRT",
            ["DataConstituicao"] = "DATA_CONSTITUICAO",
            ["Tipo"] = "TIPO",
            ["Email"] = "EMAIL",
            ["Logradouro"] = "LOGRADOURO",
            ["Numero"] = "NUMERO",
            ["Complemento"] = "COMPLEMENTO",
            ["Cep"] = "CEP",
            ["Bairro"] = "BAIRRO",
            ["Cidade"] = "CIDADE",
            ["Uf"] = "UF",
            ["Fone"] = "FONE",
            ["Contato"] = "CONTATO",
            ["CodigoIbgeCidade"] = "CODIGO_IBGE_CIDADE",
            ["CodigoIbgeUf"] = "CODIGO_IBGE_UF",
            ["Logotipo"] = "LOGOTIPO",
            ["Registrado"] = "REGISTRADO",
            ["NaturezaJuridica"] = "NATUREZA_JURIDICA",
            ["Simei"] = "SIMEI",
            ["EmailPagamento"] = "EMAIL_PAGAMENTO",
            ["DataRegistro"] = "DATA_REGISTRO",
            ["HoraRegistro"] = "HORA_REGISTRO"
        };

        public PdvSnapshotService(IConfiguration configuration)
        {
            _adminConnectionString = configuration.GetConnectionString("AdminConnection")
                ?? Biblioteca.Config?.GetConnectionString("AdminConnection")
                ?? "Server=localhost;Port=3306;Uid=root;Pwd=MySql@2025;";

            _retaguardaDatabase = ObterDatabase(
                configuration.GetConnectionString("DefaultConnection")
                ?? Biblioteca.Config?.GetConnectionString("DefaultConnection"))
                ?? "retaguarda_sh";

            _operacionalDatabaseOverride = Environment.GetEnvironmentVariable("PDV_OPERACIONAL_DATABASE")
                ?? configuration.GetValue<string>("PdvOperacionalDatabase");
        }



        public async Task<PdvSnapshotResponse> SalvarSnapshotAsync(int empresaId, string cnpj, PdvSnapshotRequest request)
        {
            if (request == null)
                throw new ArgumentException("Snapshot nao informado.");

            if (empresaId <= 0)
                throw new ArgumentException("Empresa invalida para sincronizacao.");

            cnpj = NormalizarCnpj(cnpj);
            if (!CnpjNormalizadoValido(cnpj))
                throw new ArgumentException("CNPJ invalido para sincronizacao.");

            var operacionalDatabase = NomeBancoOperacional();
            var dispositivoId = ValorOuPadrao(request.DispositivoId, "PDV-WPF");
            var tabelas = request.Tabelas
                .Where(t => !string.IsNullOrWhiteSpace(t.Nome))
                .Select(t => new PdvSnapshotTable
                {
                    Nome = NormalizarNomeTabela(t.Nome),
                    Registros = t.Registros ?? new List<PdvSnapshotRecord>()
                })
                .Where(t => !TabelasIgnoradas.Contains(t.Nome))
                .ToList();

            var agora = DateTime.UtcNow;

            await using var connection = new MySqlConnection(_adminConnectionString);
            await connection.OpenAsync();

            await GarantirBancoOperacionalAsync(connection, operacionalDatabase);
            await ExecutarAsync(connection, $"USE `{operacionalDatabase}`");
            await GarantirEstruturaAsync(connection);
            await GarantirTabelasFinaisAsync(connection, tabelas);

            await using var transaction = await connection.BeginTransactionAsync();
            try
            {
                var execucaoId = await RegistrarExecucaoAsync(
                    connection,
                    (MySqlTransaction)transaction,
                    empresaId,
                    cnpj,
                    dispositivoId,
                    tabelas,
                    agora);

                foreach (var tabela in tabelas)
                {
                    var idsAtivos = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

                    foreach (var registro in tabela.Registros)
                    {
                        var idLocal = ValorOuPadrao(registro.IdLocal, registro.Hash, Guid.NewGuid().ToString("N"));
                        idsAtivos.Add(idLocal);

                        await RegistrarSnapshotAsync(
                            connection,
                            (MySqlTransaction)transaction,
                            execucaoId,
                            empresaId,
                            tabela.Nome,
                            idLocal,
                            dispositivoId,
                            registro,
                            agora);

                        await AplicarRegistroFinalAsync(
                            connection,
                            (MySqlTransaction)transaction,
                            empresaId,
                            dispositivoId,
                            tabela.Nome,
                            idLocal,
                            registro,
                            agora);

                        await AplicarEmpresaAdministrativaAsync(
                            connection,
                            (MySqlTransaction)transaction,
                            empresaId,
                            tabela.Nome,
                            registro);
                    }

                    await MarcarAusentesComoExcluidosAsync(
                        connection,
                        (MySqlTransaction)transaction,
                        empresaId,
                        dispositivoId,
                        tabela.Nome,
                        idsAtivos,
                        agora);
                }

                await using var finalizar = new MySqlCommand(
                    "UPDATE PDV_SYNC_EXECUCAO SET FINALIZADO_EM = UTC_TIMESTAMP() WHERE ID = @id",
                    connection,
                    (MySqlTransaction)transaction);
                finalizar.Parameters.AddWithValue("@id", execucaoId);
                await finalizar.ExecuteNonQueryAsync();

                await transaction.CommitAsync();
            }
            catch
            {
                await transaction.RollbackAsync();
                throw;
            }

            return new PdvSnapshotResponse
            {
                Cnpj = cnpj,
                BancoOperacional = operacionalDatabase,
                DispositivoId = dispositivoId,
                TotalTabelas = tabelas.Count,
                TotalRegistros = tabelas.Sum(t => t.Registros.Count),
                SincronizadoEm = agora
            };
        }

        private static readonly HashSet<string> TabelasInfraestrutura = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "PDV_SYNC_EXECUCAO",
            "PDV_SYNC_REGISTRO",
            "PDV_SYNC_CONFLITO",
            "PDV_DISPOSITIVO",
            "PDV_AUDITORIA_OPERACIONAL",
            "INTEGRACAO_OUTBOX",
            "INTEGRACAO_CONFLITO",
            "INTEGRACAO_LOG"
        };

        public async Task<PdvRestoreResponse> RestaurarSnapshotAsync(int empresaId, string cnpj)
        {
            if (empresaId <= 0)
                throw new ArgumentException("Empresa invalida para restauracao.");

            cnpj = NormalizarCnpj(cnpj);
            var operacionalDatabase = NomeBancoOperacional();
            var agora = DateTime.UtcNow;

            await using var connection = new MySqlConnection(_adminConnectionString);
            await connection.OpenAsync();

            await using var checkCmd = new MySqlCommand(
                "SELECT COUNT(*) FROM INFORMATION_SCHEMA.SCHEMATA WHERE SCHEMA_NAME = @db",
                connection);
            checkCmd.Parameters.AddWithValue("@db", operacionalDatabase);
            var dbExists = Convert.ToInt32(await checkCmd.ExecuteScalarAsync()) > 0;

            if (!dbExists)
            {
                return new PdvRestoreResponse
                {
                    Cnpj = cnpj,
                    BancoOperacional = operacionalDatabase,
                    TotalTabelas = 0,
                    TotalRegistros = 0,
                    RestauradoEm = agora
                };
            }

            await ExecutarAsync(connection, $"USE `{operacionalDatabase}`");

            var nomeTabelas = await ListarTabelasOperacionaisAsync(connection);
            var resultado = new List<PdvSnapshotTable>();

            foreach (var nomeTabela in nomeTabelas)
            {
                var tabela = new PdvSnapshotTable { Nome = nomeTabela };

                await using var cmd = new MySqlCommand(
                    $"SELECT ID_LOCAL, DADOS_JSON, HASH_SHA256 FROM `{nomeTabela}` WHERE ID_EMPRESA = @empresaId AND EXCLUIDO = 'N'",
                    connection);
                cmd.Parameters.AddWithValue("@empresaId", empresaId);

                await using var reader = await cmd.ExecuteReaderAsync();
                while (await reader.ReadAsync())
                {
                    tabela.Registros.Add(new PdvSnapshotRecord
                    {
                        IdLocal = reader.IsDBNull(0) ? string.Empty : reader.GetString(0),
                        DadosJson = reader.IsDBNull(1) ? "{}" : reader.GetString(1),
                        Hash = reader.IsDBNull(2) ? string.Empty : reader.GetString(2)
                    });
                }

                if (tabela.Registros.Count > 0)
                    resultado.Add(tabela);
            }

            return new PdvRestoreResponse
            {
                Cnpj = cnpj,
                BancoOperacional = operacionalDatabase,
                TotalTabelas = resultado.Count,
                TotalRegistros = resultado.Sum(t => t.Registros.Count),
                RestauradoEm = agora,
                Tabelas = resultado
            };
        }

        private static async Task<List<string>> ListarTabelasOperacionaisAsync(MySqlConnection connection)
        {
            var tabelas = new List<string>();
            await using var cmd = new MySqlCommand(@"
                SELECT t.TABLE_NAME
                  FROM INFORMATION_SCHEMA.TABLES t
                 WHERE t.TABLE_SCHEMA = DATABASE()
                   AND (SELECT COUNT(*)
                          FROM INFORMATION_SCHEMA.COLUMNS c
                         WHERE c.TABLE_SCHEMA = t.TABLE_SCHEMA
                           AND c.TABLE_NAME   = t.TABLE_NAME
                           AND c.COLUMN_NAME IN ('ID_LOCAL','DADOS_JSON','HASH_SHA256','ID_EMPRESA','EXCLUIDO')) = 5
                 ORDER BY t.TABLE_NAME",
                connection);
            await using var reader = await cmd.ExecuteReaderAsync();
            while (await reader.ReadAsync())
            {
                var nome = reader.GetString(0);
                if (!TabelasInfraestrutura.Contains(nome))
                    tabelas.Add(nome);
            }

            return tabelas;
        }

        private async Task GarantirBancoOperacionalAsync(MySqlConnection connection, string operacionalDatabase)
        {
            await ExecutarAsync(
                connection,
                $"CREATE DATABASE IF NOT EXISTS `{operacionalDatabase}` DEFAULT CHARACTER SET utf8mb4 COLLATE utf8mb4_unicode_ci");
        }

        private static async Task<long> RegistrarExecucaoAsync(
            MySqlConnection connection,
            MySqlTransaction transaction,
            int empresaId,
            string cnpj,
            string dispositivoId,
            IReadOnlyCollection<PdvSnapshotTable> tabelas,
            DateTime agora)
        {
            await using var exec = new MySqlCommand(@"
                INSERT INTO PDV_SYNC_EXECUCAO
                    (ID_EMPRESA, CNPJ, DISPOSITIVO_ID, INICIADO_EM, TOTAL_TABELAS, TOTAL_REGISTROS)
                VALUES
                    (@empresaId, @cnpj, @dispositivo, @agora, @tabelas, @registros);
                SELECT LAST_INSERT_ID();", connection, transaction);
            exec.Parameters.AddWithValue("@empresaId", empresaId);
            exec.Parameters.AddWithValue("@cnpj", cnpj);
            exec.Parameters.AddWithValue("@dispositivo", dispositivoId);
            exec.Parameters.AddWithValue("@agora", agora);
            exec.Parameters.AddWithValue("@tabelas", tabelas.Count);
            exec.Parameters.AddWithValue("@registros", tabelas.Sum(t => t.Registros.Count));
            return Convert.ToInt64(await exec.ExecuteScalarAsync());
        }

        private static async Task RegistrarSnapshotAsync(
            MySqlConnection connection,
            MySqlTransaction transaction,
            long execucaoId,
            int empresaId,
            string tabela,
            string idLocal,
            string dispositivoId,
            PdvSnapshotRecord registro,
            DateTime agora)
        {
            await using var insert = new MySqlCommand(@"
                INSERT INTO PDV_SYNC_REGISTRO
                    (ID_EXECUCAO, ID_EMPRESA, TABELA, ID_LOCAL, DISPOSITIVO_ID, DADOS_JSON, HASH_SHA256, SINCRONIZADO_EM)
                VALUES
                    (@execucao, @empresaId, @tabela, @idLocal, @dispositivoId, @dados, @hash, @agora)
                ON DUPLICATE KEY UPDATE
                    ID_EXECUCAO = VALUES(ID_EXECUCAO),
                    DADOS_JSON = VALUES(DADOS_JSON),
                    HASH_SHA256 = VALUES(HASH_SHA256),
                    SINCRONIZADO_EM = VALUES(SINCRONIZADO_EM)", connection, transaction);
            insert.Parameters.AddWithValue("@execucao", execucaoId);
            insert.Parameters.AddWithValue("@empresaId", empresaId);
            insert.Parameters.AddWithValue("@tabela", tabela);
            insert.Parameters.AddWithValue("@idLocal", idLocal);
            insert.Parameters.AddWithValue("@dispositivoId", dispositivoId);
            insert.Parameters.AddWithValue("@dados", registro.DadosJson ?? "{}");
            insert.Parameters.AddWithValue("@hash", ValorOuPadrao(registro.Hash, string.Empty));
            insert.Parameters.AddWithValue("@agora", agora);
            await insert.ExecuteNonQueryAsync();
        }

        private static async Task GarantirEstruturaAsync(MySqlConnection connection)
        {
            await ExecutarAsync(connection, @"
                CREATE TABLE IF NOT EXISTS PDV_SYNC_EXECUCAO (
                    ID BIGINT UNSIGNED NOT NULL AUTO_INCREMENT,
                    ID_EMPRESA INT UNSIGNED NOT NULL,
                    CNPJ VARCHAR(14) NOT NULL,
                    DISPOSITIVO_ID VARCHAR(64) NOT NULL,
                    INICIADO_EM DATETIME NOT NULL,
                    FINALIZADO_EM DATETIME NULL,
                    TOTAL_TABELAS INT NOT NULL DEFAULT 0,
                    TOTAL_REGISTROS INT NOT NULL DEFAULT 0,
                    PRIMARY KEY (ID),
                    INDEX IX_PDV_SYNC_EXECUCAO_EMPRESA (ID_EMPRESA)
                ) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4 COLLATE=utf8mb4_unicode_ci");

            await GarantirColunaAsync(connection, "PDV_SYNC_EXECUCAO", "ID_EMPRESA", "INT UNSIGNED NOT NULL DEFAULT 0 AFTER ID");
            await GarantirIndiceAsync(connection, "PDV_SYNC_EXECUCAO", "IX_PDV_SYNC_EXECUCAO_EMPRESA", "CREATE INDEX IX_PDV_SYNC_EXECUCAO_EMPRESA ON PDV_SYNC_EXECUCAO (ID_EMPRESA)");

            await ExecutarAsync(connection, @"
                CREATE TABLE IF NOT EXISTS PDV_SYNC_REGISTRO (
                    ID BIGINT UNSIGNED NOT NULL AUTO_INCREMENT,
                    ID_EXECUCAO BIGINT UNSIGNED NOT NULL,
                    ID_EMPRESA INT UNSIGNED NOT NULL,
                    TABELA VARCHAR(128) NOT NULL,
                    ID_LOCAL VARCHAR(128) NOT NULL,
                    DISPOSITIVO_ID VARCHAR(64) NOT NULL,
                    DADOS_JSON LONGTEXT NOT NULL,
                    HASH_SHA256 VARCHAR(64) NOT NULL,
                    SINCRONIZADO_EM DATETIME NOT NULL,
                    PRIMARY KEY (ID),
                    UNIQUE KEY UK_PDV_SYNC_REGISTRO_TENANT_TABELA_ID_DEVICE (ID_EMPRESA, TABELA, ID_LOCAL, DISPOSITIVO_ID),
                    INDEX IX_PDV_SYNC_REGISTRO_EXECUCAO (ID_EXECUCAO),
                    INDEX IX_PDV_SYNC_REGISTRO_EMPRESA (ID_EMPRESA)
                ) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4 COLLATE=utf8mb4_unicode_ci");

            await GarantirColunaAsync(connection, "PDV_SYNC_REGISTRO", "ID_EMPRESA", "INT UNSIGNED NOT NULL DEFAULT 0 AFTER ID_EXECUCAO");
            await GarantirColunaAsync(connection, "PDV_SYNC_REGISTRO", "DISPOSITIVO_ID", "VARCHAR(64) NOT NULL DEFAULT 'PDV-WPF' AFTER ID_LOCAL");
            await RemoverIndiceAsync(connection, "PDV_SYNC_REGISTRO", "UK_PDV_SYNC_REGISTRO_TENANT_TABELA_ID");
            await GarantirIndiceAsync(connection, "PDV_SYNC_REGISTRO", "UK_PDV_SYNC_REGISTRO_TENANT_TABELA_ID_DEVICE", "CREATE UNIQUE INDEX UK_PDV_SYNC_REGISTRO_TENANT_TABELA_ID_DEVICE ON PDV_SYNC_REGISTRO (ID_EMPRESA, TABELA, ID_LOCAL, DISPOSITIVO_ID)");
            await GarantirIndiceAsync(connection, "PDV_SYNC_REGISTRO", "IX_PDV_SYNC_REGISTRO_EMPRESA", "CREATE INDEX IX_PDV_SYNC_REGISTRO_EMPRESA ON PDV_SYNC_REGISTRO (ID_EMPRESA)");

            await ExecutarAsync(connection, @"
                CREATE TABLE IF NOT EXISTS PDV_SYNC_CONFLITO (
                    ID BIGINT UNSIGNED NOT NULL AUTO_INCREMENT,
                    ID_EMPRESA INT UNSIGNED NOT NULL,
                    TABELA VARCHAR(128) NOT NULL,
                    ID_LOCAL VARCHAR(128) NOT NULL,
                    DISPOSITIVO_ID VARCHAR(64) NOT NULL,
                    DADOS_LOCAL LONGTEXT NULL,
                    DADOS_REMOTO LONGTEXT NULL,
                    MOTIVO VARCHAR(500) NULL,
                    STATUS VARCHAR(30) NOT NULL DEFAULT 'PENDENTE',
                    CRIADO_EM DATETIME NOT NULL,
                    RESOLVIDO_EM DATETIME NULL,
                    PRIMARY KEY (ID),
                    INDEX IX_PDV_SYNC_CONFLITO_EMPRESA (ID_EMPRESA)
                ) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4 COLLATE=utf8mb4_unicode_ci");

            await ExecutarAsync(connection, @"
                CREATE TABLE IF NOT EXISTS PDV_DISPOSITIVO (
                    ID BIGINT UNSIGNED NOT NULL AUTO_INCREMENT,
                    ID_EMPRESA INT UNSIGNED NOT NULL,
                    DISPOSITIVO_ID VARCHAR(64) NOT NULL,
                    NOME VARCHAR(120) NULL,
                    ULTIMA_SINCRONIZACAO_EM DATETIME NULL,
                    ATIVO CHAR(1) NOT NULL DEFAULT 'S',
                    PRIMARY KEY (ID),
                    UNIQUE KEY UK_PDV_DISPOSITIVO_EMPRESA_DISPOSITIVO (ID_EMPRESA, DISPOSITIVO_ID)
                ) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4 COLLATE=utf8mb4_unicode_ci");

            await ExecutarAsync(connection, @"
                CREATE TABLE IF NOT EXISTS PDV_AUDITORIA_OPERACIONAL (
                    ID BIGINT UNSIGNED NOT NULL AUTO_INCREMENT,
                    ID_EMPRESA INT UNSIGNED NOT NULL,
                    TABELA VARCHAR(128) NOT NULL,
                    ID_LOCAL VARCHAR(128) NOT NULL,
                    DISPOSITIVO_ID VARCHAR(64) NOT NULL,
                    ACAO VARCHAR(30) NOT NULL,
                    DADOS_JSON LONGTEXT NULL,
                    CRIADO_EM DATETIME NOT NULL,
                    PRIMARY KEY (ID),
                    INDEX IX_PDV_AUDITORIA_EMPRESA_TABELA (ID_EMPRESA, TABELA)
                ) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4 COLLATE=utf8mb4_unicode_ci");

            await ExecutarAsync(connection, @"
                CREATE TABLE IF NOT EXISTS INTEGRACAO_OUTBOX (
                    ID BIGINT UNSIGNED NOT NULL AUTO_INCREMENT,
                    ID_EMPRESA INT UNSIGNED NOT NULL,
                    ORIGEM VARCHAR(60) NOT NULL,
                    ENTIDADE VARCHAR(128) NOT NULL,
                    ID_LOCAL VARCHAR(128) NOT NULL,
                    PAYLOAD_JSON LONGTEXT NOT NULL,
                    STATUS VARCHAR(30) NOT NULL DEFAULT 'PROCESSADO',
                    CRIADO_EM DATETIME NOT NULL,
                    PROCESSADO_EM DATETIME NULL,
                    PRIMARY KEY (ID),
                    INDEX IX_INTEGRACAO_OUTBOX_EMPRESA_STATUS (ID_EMPRESA, STATUS)
                ) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4 COLLATE=utf8mb4_unicode_ci");

            await ExecutarAsync(connection, @"
                CREATE TABLE IF NOT EXISTS INTEGRACAO_CONFLITO (
                    ID BIGINT UNSIGNED NOT NULL AUTO_INCREMENT,
                    ID_EMPRESA INT UNSIGNED NOT NULL,
                    ENTIDADE VARCHAR(128) NOT NULL,
                    ID_LOCAL VARCHAR(128) NOT NULL,
                    MOTIVO VARCHAR(500) NULL,
                    PAYLOAD_JSON LONGTEXT NULL,
                    STATUS VARCHAR(30) NOT NULL DEFAULT 'PENDENTE',
                    CRIADO_EM DATETIME NOT NULL,
                    PRIMARY KEY (ID),
                    INDEX IX_INTEGRACAO_CONFLITO_EMPRESA (ID_EMPRESA)
                ) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4 COLLATE=utf8mb4_unicode_ci");

            await ExecutarAsync(connection, @"
                CREATE TABLE IF NOT EXISTS INTEGRACAO_LOG (
                    ID BIGINT UNSIGNED NOT NULL AUTO_INCREMENT,
                    ID_EMPRESA INT UNSIGNED NOT NULL,
                    ENTIDADE VARCHAR(128) NOT NULL,
                    ID_LOCAL VARCHAR(128) NULL,
                    EVENTO VARCHAR(60) NOT NULL,
                    MENSAGEM VARCHAR(1000) NULL,
                    CRIADO_EM DATETIME NOT NULL,
                    PRIMARY KEY (ID),
                    INDEX IX_INTEGRACAO_LOG_EMPRESA (ID_EMPRESA)
                ) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4 COLLATE=utf8mb4_unicode_ci");
        }

        private static async Task GarantirTabelasFinaisAsync(MySqlConnection connection, IEnumerable<PdvSnapshotTable> tabelas)
        {
            var database = await ObterDatabaseAtualAsync(connection);
            foreach (var tabela in tabelas)
            {
                if (tabela.Nome.StartsWith("PDV_SYNC_", StringComparison.OrdinalIgnoreCase))
                    continue;

                var colunas = ExtrairColunas(tabela)
                    .Select(NormalizarNomeColuna)
                    .Where(c => !string.IsNullOrWhiteSpace(c))
                    .Where(c => !EhColunaReservada(c))
                    .Distinct(StringComparer.OrdinalIgnoreCase)
                    .ToList();

                if (!await TabelaExisteAsync(connection, database, tabela.Nome))
                {
                    var colunasPayloadSql = colunas.Count == 0
                        ? string.Empty
                        : string.Join(Environment.NewLine, colunas.Select(c => $"                        `{c}` LONGTEXT NULL,"));

                    await ExecutarAsync(connection, $@"
                    CREATE TABLE `{tabela.Nome}` (
                        `ID` BIGINT UNSIGNED NOT NULL AUTO_INCREMENT,
                        `ID_EMPRESA` INT UNSIGNED NOT NULL,
                        `ID_LOCAL` VARCHAR(128) NOT NULL,
                        `DISPOSITIVO_ID` VARCHAR(64) NOT NULL,
                        `SINCRONIZADO_EM` DATETIME NOT NULL,
                        `HASH_SHA256` VARCHAR(64) NULL,
                        `DADOS_JSON` LONGTEXT NULL,
                        `EXCLUIDO` CHAR(1) NOT NULL DEFAULT 'N',
{colunasPayloadSql}
                        PRIMARY KEY (`ID`),
                        UNIQUE KEY `UK_{tabela.Nome}_TENANT_LOCAL_DEVICE` (`ID_EMPRESA`, `ID_LOCAL`, `DISPOSITIVO_ID`),
                        INDEX `IX_{tabela.Nome}_EMPRESA` (`ID_EMPRESA`)
                    ) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4 COLLATE=utf8mb4_unicode_ci");
                }
                else
                {
                    await GarantirColunaAsync(connection, tabela.Nome, "ID_EMPRESA", "INT UNSIGNED NOT NULL DEFAULT 0 AFTER ID");
                    await GarantirColunaAsync(connection, tabela.Nome, "DISPOSITIVO_ID", "VARCHAR(64) NOT NULL DEFAULT 'PDV-WPF' AFTER ID_LOCAL");
                    await GarantirColunaAsync(connection, tabela.Nome, "HASH_SHA256", "VARCHAR(64) NULL AFTER SINCRONIZADO_EM");
                    await GarantirColunaAsync(connection, tabela.Nome, "DADOS_JSON", "LONGTEXT NULL AFTER HASH_SHA256");
                    await GarantirColunaAsync(connection, tabela.Nome, "EXCLUIDO", "CHAR(1) NOT NULL DEFAULT 'N' AFTER DADOS_JSON");
                    await GarantirIndiceAsync(connection, tabela.Nome, $"UK_{tabela.Nome}_TENANT_LOCAL_DEVICE", $"CREATE UNIQUE INDEX `UK_{tabela.Nome}_TENANT_LOCAL_DEVICE` ON `{tabela.Nome}` (`ID_EMPRESA`, `ID_LOCAL`, `DISPOSITIVO_ID`)");
                    await GarantirIndiceAsync(connection, tabela.Nome, $"IX_{tabela.Nome}_EMPRESA", $"CREATE INDEX `IX_{tabela.Nome}_EMPRESA` ON `{tabela.Nome}` (`ID_EMPRESA`)");

                    foreach (var coluna in colunas)
                        await GarantirColunaAsync(connection, tabela.Nome, coluna, "LONGTEXT NULL");
                }
            }
        }

        private static async Task AplicarRegistroFinalAsync(
            MySqlConnection connection,
            MySqlTransaction transaction,
            int empresaId,
            string dispositivoId,
            string nomeTabela,
            string idLocal,
            PdvSnapshotRecord registro,
            DateTime agora)
        {
            if (nomeTabela.StartsWith("PDV_SYNC_", StringComparison.OrdinalIgnoreCase))
                return;

            var dados = LerDados(registro.DadosJson);
            var colunas = dados.Keys
                .Select(NormalizarNomeColuna)
                .Where(c => !string.IsNullOrWhiteSpace(c))
                .Where(c => !EhColunaReservada(c))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();

            var nomesColunas = new List<string>
            {
                "ID_EMPRESA",
                "ID_LOCAL",
                "DISPOSITIVO_ID",
                "SINCRONIZADO_EM",
                "HASH_SHA256",
                "DADOS_JSON",
                "EXCLUIDO"
            };
            nomesColunas.AddRange(colunas);

            var parametros = nomesColunas.Select((_, index) => $"@p{index}").ToList();
            var atualizacoes = nomesColunas
                .Where(c => !c.Equals("ID_EMPRESA", StringComparison.OrdinalIgnoreCase))
                .Where(c => !c.Equals("ID_LOCAL", StringComparison.OrdinalIgnoreCase))
                .Where(c => !c.Equals("DISPOSITIVO_ID", StringComparison.OrdinalIgnoreCase))
                .Select(c => $"`{c}` = VALUES(`{c}`)");

            var sql = $@"
                INSERT INTO `{nomeTabela}`
                    ({string.Join(", ", nomesColunas.Select(c => $"`{c}`"))})
                VALUES
                    ({string.Join(", ", parametros)})
                ON DUPLICATE KEY UPDATE
                    {string.Join(", ", atualizacoes)}";

            await using var command = new MySqlCommand(sql, connection, transaction);
            command.Parameters.AddWithValue("@p0", empresaId);
            command.Parameters.AddWithValue("@p1", idLocal);
            command.Parameters.AddWithValue("@p2", dispositivoId);
            command.Parameters.AddWithValue("@p3", agora);
            command.Parameters.AddWithValue("@p4", ValorOuPadrao(registro.Hash, string.Empty));
            command.Parameters.AddWithValue("@p5", registro.DadosJson ?? "{}");
            command.Parameters.AddWithValue("@p6", "N");

            for (var i = 0; i < colunas.Count; i++)
            {
                var colunaOriginal = dados.Keys.First(k => NormalizarNomeColuna(k).Equals(colunas[i], StringComparison.OrdinalIgnoreCase));
                command.Parameters.AddWithValue($"@p{i + 7}", ConverterValorFinal(colunaOriginal, dados[colunaOriginal]));
            }

            await command.ExecuteNonQueryAsync();

            await RegistrarAuditoriaAsync(connection, transaction, empresaId, dispositivoId, nomeTabela, idLocal, "UPSERT", registro.DadosJson, agora);
            await RegistrarOutboxProcessadaAsync(connection, transaction, empresaId, nomeTabela, idLocal, registro.DadosJson, agora);
            await RegistrarDispositivoAsync(connection, transaction, empresaId, dispositivoId, agora);
        }

        private static async Task MarcarAusentesComoExcluidosAsync(
            MySqlConnection connection,
            MySqlTransaction transaction,
            int empresaId,
            string dispositivoId,
            string nomeTabela,
            HashSet<string> idsAtivos,
            DateTime agora)
        {
            if (nomeTabela.StartsWith("PDV_SYNC_", StringComparison.OrdinalIgnoreCase))
                return;

            var parametros = idsAtivos.Select((_, index) => $"@id{index}").ToList();
            var filtroIds = idsAtivos.Count == 0
                ? string.Empty
                : $"AND ID_LOCAL NOT IN ({string.Join(", ", parametros)})";

            var sql = $@"
                UPDATE `{nomeTabela}`
                   SET EXCLUIDO = 'S',
                       SINCRONIZADO_EM = @agora
                 WHERE ID_EMPRESA = @empresaId
                   AND DISPOSITIVO_ID = @dispositivoId
                   {filtroIds}";

            await using var command = new MySqlCommand(sql, connection, transaction);
            command.Parameters.AddWithValue("@agora", agora);
            command.Parameters.AddWithValue("@empresaId", empresaId);
            command.Parameters.AddWithValue("@dispositivoId", dispositivoId);

            var i = 0;
            foreach (var id in idsAtivos)
                command.Parameters.AddWithValue($"@id{i++}", id);

            await command.ExecuteNonQueryAsync();
        }

        private async Task AplicarEmpresaAdministrativaAsync(
            MySqlConnection connection,
            MySqlTransaction transaction,
            int empresaId,
            string nomeTabela,
            PdvSnapshotRecord registro)
        {
            if (!nomeTabela.Equals("EMPRESA", StringComparison.OrdinalIgnoreCase))
                return;

            if (!await TabelaExisteAsync(connection, _retaguardaDatabase, "EMPRESA"))
                return;

            var dados = LerDados(registro.DadosJson);
            var colunasExistentes = await ListarColunasAsync(connection, _retaguardaDatabase, "EMPRESA");
            var pares = EmpresaColunas
                .Where(p => dados.ContainsKey(p.Key))
                .Where(p => colunasExistentes.Contains(p.Value))
                .Where(p => !p.Value.Equals("CNPJ", StringComparison.OrdinalIgnoreCase))
                .ToList();

            if (pares.Count == 0)
                return;

            var set = string.Join(", ", pares.Select((p, index) => $"`{p.Value}` = @p{index}"));
            await using var update = new MySqlCommand(
                $"UPDATE `{_retaguardaDatabase}`.`EMPRESA` SET {set} WHERE ID = @id",
                connection,
                transaction);

            for (var i = 0; i < pares.Count; i++)
                update.Parameters.AddWithValue($"@p{i}", ConverterValorFinal(pares[i].Key, dados[pares[i].Key]));

            update.Parameters.AddWithValue("@id", empresaId);
            await update.ExecuteNonQueryAsync();
        }

        private static async Task RegistrarAuditoriaAsync(
            MySqlConnection connection,
            MySqlTransaction transaction,
            int empresaId,
            string dispositivoId,
            string tabela,
            string idLocal,
            string acao,
            string dadosJson,
            DateTime agora)
        {
            await using var command = new MySqlCommand(@"
                INSERT INTO PDV_AUDITORIA_OPERACIONAL
                    (ID_EMPRESA, TABELA, ID_LOCAL, DISPOSITIVO_ID, ACAO, DADOS_JSON, CRIADO_EM)
                VALUES
                    (@empresaId, @tabela, @idLocal, @dispositivoId, @acao, @dadosJson, @agora)", connection, transaction);
            command.Parameters.AddWithValue("@empresaId", empresaId);
            command.Parameters.AddWithValue("@tabela", tabela);
            command.Parameters.AddWithValue("@idLocal", idLocal);
            command.Parameters.AddWithValue("@dispositivoId", dispositivoId);
            command.Parameters.AddWithValue("@acao", acao);
            command.Parameters.AddWithValue("@dadosJson", dadosJson ?? "{}");
            command.Parameters.AddWithValue("@agora", agora);
            await command.ExecuteNonQueryAsync();
        }

        private static async Task RegistrarOutboxProcessadaAsync(
            MySqlConnection connection,
            MySqlTransaction transaction,
            int empresaId,
            string tabela,
            string idLocal,
            string dadosJson,
            DateTime agora)
        {
            await using var command = new MySqlCommand(@"
                INSERT INTO INTEGRACAO_OUTBOX
                    (ID_EMPRESA, ORIGEM, ENTIDADE, ID_LOCAL, PAYLOAD_JSON, STATUS, CRIADO_EM, PROCESSADO_EM)
                VALUES
                    (@empresaId, 'PDV', @entidade, @idLocal, @payload, 'PROCESSADO', @agora, @agora)", connection, transaction);
            command.Parameters.AddWithValue("@empresaId", empresaId);
            command.Parameters.AddWithValue("@entidade", tabela);
            command.Parameters.AddWithValue("@idLocal", idLocal);
            command.Parameters.AddWithValue("@payload", dadosJson ?? "{}");
            command.Parameters.AddWithValue("@agora", agora);
            await command.ExecuteNonQueryAsync();
        }

        private static async Task RegistrarDispositivoAsync(
            MySqlConnection connection,
            MySqlTransaction transaction,
            int empresaId,
            string dispositivoId,
            DateTime agora)
        {
            await using var command = new MySqlCommand(@"
                INSERT INTO PDV_DISPOSITIVO
                    (ID_EMPRESA, DISPOSITIVO_ID, NOME, ULTIMA_SINCRONIZACAO_EM, ATIVO)
                VALUES
                    (@empresaId, @dispositivoId, @dispositivoId, @agora, 'S')
                ON DUPLICATE KEY UPDATE
                    ULTIMA_SINCRONIZACAO_EM = VALUES(ULTIMA_SINCRONIZACAO_EM),
                    ATIVO = 'S'", connection, transaction);
            command.Parameters.AddWithValue("@empresaId", empresaId);
            command.Parameters.AddWithValue("@dispositivoId", dispositivoId);
            command.Parameters.AddWithValue("@agora", agora);
            await command.ExecuteNonQueryAsync();
        }

        private static async Task<bool> TabelaExisteAsync(MySqlConnection connection, string database, string tabela)
        {
            await using var command = new MySqlCommand(@"
                SELECT COUNT(*)
                  FROM INFORMATION_SCHEMA.TABLES
                 WHERE TABLE_SCHEMA = @database
                   AND TABLE_NAME = @tabela", connection);
            command.Parameters.AddWithValue("@database", database);
            command.Parameters.AddWithValue("@tabela", tabela);
            return Convert.ToInt32(await command.ExecuteScalarAsync()) > 0;
        }

        private static async Task<string> ObterDatabaseAtualAsync(MySqlConnection connection)
        {
            await using var command = new MySqlCommand("SELECT DATABASE()", connection);
            var database = Convert.ToString(await command.ExecuteScalarAsync());
            if (string.IsNullOrWhiteSpace(database))
                throw new InvalidOperationException("Nenhum database selecionado para sincronizacao do PDV.");

            return database;
        }

        private static async Task<HashSet<string>> ListarColunasAsync(MySqlConnection connection, string database, string tabela)
        {
            var colunas = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            await using var command = new MySqlCommand(@"
                SELECT COLUMN_NAME
                  FROM INFORMATION_SCHEMA.COLUMNS
                 WHERE TABLE_SCHEMA = @database
                   AND TABLE_NAME = @tabela", connection);
            command.Parameters.AddWithValue("@database", database);
            command.Parameters.AddWithValue("@tabela", tabela);

            await using var reader = await command.ExecuteReaderAsync();
            while (await reader.ReadAsync())
                colunas.Add(reader.GetString(0));

            return colunas;
        }

        private static async Task GarantirColunaAsync(MySqlConnection connection, string tabela, string coluna, string definicao)
        {
            if (!await ColunaExisteAsync(connection, tabela, coluna))
                await ExecutarAsync(connection, $"ALTER TABLE `{tabela}` ADD COLUMN `{coluna}` {definicao}");
        }

        private static async Task<bool> ColunaExisteAsync(MySqlConnection connection, string tabela, string coluna)
        {
            await using var command = new MySqlCommand(@"
                SELECT COUNT(*)
                  FROM INFORMATION_SCHEMA.COLUMNS
                 WHERE TABLE_SCHEMA = DATABASE()
                   AND TABLE_NAME = @tabela
                   AND COLUMN_NAME = @coluna", connection);
            command.Parameters.AddWithValue("@tabela", tabela);
            command.Parameters.AddWithValue("@coluna", coluna);
            return Convert.ToInt32(await command.ExecuteScalarAsync()) > 0;
        }

        private static async Task GarantirIndiceAsync(MySqlConnection connection, string tabela, string indice, string createSql)
        {
            await using var command = new MySqlCommand(@"
                SELECT COUNT(*)
                  FROM INFORMATION_SCHEMA.STATISTICS
                 WHERE TABLE_SCHEMA = DATABASE()
                   AND TABLE_NAME = @tabela
                   AND INDEX_NAME = @indice", connection);
            command.Parameters.AddWithValue("@tabela", tabela);
            command.Parameters.AddWithValue("@indice", indice);

            if (Convert.ToInt32(await command.ExecuteScalarAsync()) == 0)
                await ExecutarAsync(connection, createSql);
        }

        private static async Task RemoverIndiceAsync(MySqlConnection connection, string tabela, string indice)
        {
            await using var command = new MySqlCommand(@"
                SELECT COUNT(*)
                  FROM INFORMATION_SCHEMA.STATISTICS
                 WHERE TABLE_SCHEMA = DATABASE()
                   AND TABLE_NAME = @tabela
                   AND INDEX_NAME = @indice", connection);
            command.Parameters.AddWithValue("@tabela", tabela);
            command.Parameters.AddWithValue("@indice", indice);

            if (Convert.ToInt32(await command.ExecuteScalarAsync()) > 0)
                await ExecutarAsync(connection, $"DROP INDEX `{indice}` ON `{tabela}`");
        }

        private static IReadOnlyList<string> ExtrairColunas(PdvSnapshotTable tabela)
        {
            var colunas = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var registro in tabela.Registros)
            {
                foreach (var coluna in LerDados(registro.DadosJson).Keys)
                    colunas.Add(coluna);
            }

            return colunas.ToList();
        }

        private static Dictionary<string, object> LerDados(string dadosJson)
        {
            var dados = new Dictionary<string, object>(StringComparer.OrdinalIgnoreCase);
            if (string.IsNullOrWhiteSpace(dadosJson))
                return dados;

            using var document = JsonDocument.Parse(dadosJson);
            if (document.RootElement.ValueKind != JsonValueKind.Object)
                return dados;

            foreach (var property in document.RootElement.EnumerateObject())
                dados[property.Name] = ConverterValorJson(property.Value);

            return dados;
        }

        private static object ConverterValorJson(JsonElement value)
        {
            return value.ValueKind switch
            {
                JsonValueKind.Null => DBNull.Value,
                JsonValueKind.Undefined => DBNull.Value,
                JsonValueKind.True => "S",
                JsonValueKind.False => "N",
                JsonValueKind.Number => value.GetRawText(),
                JsonValueKind.String => value.GetString() ?? string.Empty,
                _ => value.GetRawText()
            };
        }

        private static object ConverterValorFinal(string propriedade, object valor)
        {
            if (valor == null || valor == DBNull.Value)
                return DBNull.Value;

            if (propriedade.EndsWith("Cnpj", StringComparison.OrdinalIgnoreCase) ||
                propriedade.Equals("CpfCnpj", StringComparison.OrdinalIgnoreCase))
            {
                return NormalizarCnpj(valor.ToString() ?? string.Empty);
            }

            if (propriedade.Equals("Cpf", StringComparison.OrdinalIgnoreCase) ||
                propriedade.Equals("Cep", StringComparison.OrdinalIgnoreCase))
            {
                return ApenasDigitos(valor.ToString() ?? string.Empty);
            }

            return valor;
        }

        private static string ObterDatabase(string connectionString)
        {
            if (string.IsNullOrWhiteSpace(connectionString))
                return null;

            var builder = new MySqlConnectionStringBuilder(connectionString);
            return string.IsNullOrWhiteSpace(builder.Database) ? null : builder.Database;
        }

        private string NomeBancoOperacional()
        {
            if (!string.IsNullOrWhiteSpace(_operacionalDatabaseOverride))
                return NormalizarNomeDatabase(_operacionalDatabaseOverride);

            return "pdv_operacional";
        }

        private static string NormalizarNomeDatabase(string nome)
        {
            var normalizado = Regex.Replace((nome ?? string.Empty).Trim().ToLowerInvariant(), "[^a-z0-9_]", "_");
            normalizado = Regex.Replace(normalizado, "_+", "_").Trim('_');
            return string.IsNullOrWhiteSpace(normalizado) ? "pdv_operacional" : normalizado;
        }

        private static async Task ExecutarAsync(MySqlConnection connection, string sql)
        {
            await using var command = new MySqlCommand(sql, connection);
            await command.ExecuteNonQueryAsync();
        }

        private static string ApenasDigitos(string valor)
        {
            return new string((valor ?? string.Empty).Where(char.IsDigit).ToArray());
        }

        private static string NormalizarCnpj(string valor)
        {
            if (string.IsNullOrWhiteSpace(valor))
                return string.Empty;

            var caracteres = new List<char>();
            foreach (var c in valor)
            {
                if (char.IsDigit(c))
                {
                    caracteres.Add(c);
                    continue;
                }

                var letra = char.ToUpperInvariant(c);
                if (letra >= 'A' && letra <= 'Z')
                    caracteres.Add(letra);
            }

            return new string(caracteres.ToArray());
        }

        private static bool CnpjNormalizadoValido(string cnpj)
        {
            return cnpj.Length == 14 && cnpj.All(c => char.IsDigit(c) || (c >= 'A' && c <= 'Z'));
        }

        private static string NormalizarNomeTabela(string nome)
        {
            var normalizado = Regex.Replace((nome ?? string.Empty).Trim().ToUpperInvariant(), "[^A-Z0-9_]", "_");
            return string.IsNullOrWhiteSpace(normalizado) ? "TABELA_SEM_NOME" : normalizado;
        }

        private static string NormalizarNomeColuna(string nome)
        {
            var comUnderscore = Regex.Replace((nome ?? string.Empty).Trim(), "([a-z0-9])([A-Z])", "$1_$2");
            var normalizado = Regex.Replace(comUnderscore.ToUpperInvariant(), "[^A-Z0-9_]", "_");
            normalizado = Regex.Replace(normalizado, "_+", "_").Trim('_');
            return string.IsNullOrWhiteSpace(normalizado) ? string.Empty : normalizado;
        }

        private static bool EhColunaReservada(string coluna)
        {
            return coluna.Equals("ID", StringComparison.OrdinalIgnoreCase)
                || coluna.Equals("ID_EMPRESA", StringComparison.OrdinalIgnoreCase)
                || coluna.Equals("ID_LOCAL", StringComparison.OrdinalIgnoreCase)
                || coluna.Equals("DISPOSITIVO_ID", StringComparison.OrdinalIgnoreCase)
                || coluna.Equals("SINCRONIZADO_EM", StringComparison.OrdinalIgnoreCase)
                || coluna.Equals("HASH_SHA256", StringComparison.OrdinalIgnoreCase)
                || coluna.Equals("DADOS_JSON", StringComparison.OrdinalIgnoreCase)
                || coluna.Equals("EXCLUIDO", StringComparison.OrdinalIgnoreCase);
        }

        private static string ValorOuPadrao(params string[] valores)
        {
            foreach (var valor in valores)
            {
                if (!string.IsNullOrWhiteSpace(valor))
                    return valor.Trim();
            }

            return string.Empty;
        }
    }
}
