using Microsoft.Extensions.Configuration;
using MySql.Data.MySqlClient;
using System;
using System.Security.Cryptography;
using System.Text;
using System.Threading.Tasks;
using T2TiRetaguardaSH.Models.Auth;
using T2TiRetaguardaSH.Util;

namespace T2TiRetaguardaSH.Services.Auth
{
    public class AuthService
    {
        private readonly string _connectionString;

        public AuthService(IConfiguration configuration)
        {
            _connectionString = configuration.GetConnectionString("DefaultConnection")
                ?? Biblioteca.Config?.GetConnectionString("DefaultConnection")
                ?? "Server=localhost;Port=3306;Database=retaguarda_sh;Uid=t2ti_user;Pwd=123456;";
        }

        public async Task<AuthResponse> CriarContaAsync(CriarContaRequest request)
        {
            ValidarCriacao(request);
            var cnpj = CnpjUtils.Normalizar(request.Cnpj);
            var email = NormalizarLogin(request.Email);
            var login = string.IsNullOrWhiteSpace(request.Login) ? email : NormalizarLogin(request.Login);
            var senha = request.Senha.Trim();
            var perfil = string.IsNullOrWhiteSpace(request.Perfil) ? "Administrador" : request.Perfil.Trim();

            await using var connection = new MySqlConnection(_connectionString);
            await connection.OpenAsync();
            await GarantirTabelasAsync(connection);

            if (await ObterUsuarioIdPorEmailAsync(connection, email) != null)
                throw new InvalidOperationException("E-mail ja cadastrado no sistema. Use outro e-mail ou recupere a senha.");

            var empresaId = await ObterEmpresaIdAsync(connection, cnpj);
            if (empresaId == null)
            {
                empresaId = await InserirEmpresaAsync(connection, cnpj, request);
            }

            var usuarioExistente = await ObterUsuarioIdAsync(connection, empresaId.Value, login);
            if (usuarioExistente != null)
            {
                var confirmado = await ObterUsuarioConfirmadoAsync(connection, usuarioExistente.Value);
                if (string.Equals(confirmado, "S", StringComparison.OrdinalIgnoreCase))
                    throw new InvalidOperationException("Usuario ja cadastrado para esta empresa.");

                await AtualizarUsuarioPendenteAsync(connection, usuarioExistente.Value, request.UsuarioNome, email, perfil);
                return await MontarRespostaAsync(connection, empresaId.Value, usuarioExistente.Value, string.Empty, null);
            }

            var salt = CriarTokenSeguro(24);
            var senhaHash = HashSenha(senha, salt);
            var usuarioId = await InserirUsuarioAsync(connection, empresaId.Value, request.UsuarioNome, login, senhaHash, salt, perfil, email);

            return await MontarRespostaAsync(connection, empresaId.Value, usuarioId, string.Empty, null);
        }

        public async Task<AuthResponse> LoginAsync(LoginRequest request)
        {
            if (request == null)
                throw new ArgumentException("Informe os dados de login.");

            var email = NormalizarLogin(request.Email);
            var senha = request.Senha?.Trim() ?? "";

            if (string.IsNullOrWhiteSpace(email) || email.IndexOf('@') <= 0 || string.IsNullOrWhiteSpace(senha))
                throw new ArgumentException("E-mail e senha sao obrigatorios.");

            await using var connection = new MySqlConnection(_connectionString);
            await connection.OpenAsync();
            await GarantirTabelasAsync(connection);

            const string sql = @"
                SELECT u.ID, u.ID_EMPRESA, u.SENHA_HASH, u.SENHA_SALT, COALESCE(u.CONFIRMADO, 'S') AS CONFIRMADO, COALESCE(e.REGISTRADO, 'S') AS REGISTRADO
                  FROM RET_USUARIO u
                  JOIN EMPRESA e ON e.ID = u.ID_EMPRESA
                 WHERE LOWER(u.EMAIL) = @email
                   AND u.ATIVO = 'S'
                 LIMIT 1";

            await using var command = new MySqlCommand(sql, connection);
            command.Parameters.AddWithValue("@email", email);

            await using var reader = await command.ExecuteReaderAsync();
            if (!await reader.ReadAsync())
                throw new UnauthorizedAccessException("Usuario ou senha invalidos.");

            var usuarioId = Convert.ToInt32(reader["ID"]);
            var empresaId = Convert.ToInt32(reader["ID_EMPRESA"]);
            var senhaHash = reader["SENHA_HASH"].ToString();
            var salt = reader["SENHA_SALT"].ToString();
            var usuarioConfirmado = reader["CONFIRMADO"].ToString();
            var empresaRegistrada = reader["REGISTRADO"].ToString();
            await reader.CloseAsync();

            if (!string.Equals(HashSenha(senha, salt), senhaHash, StringComparison.Ordinal))
                throw new UnauthorizedAccessException("Usuario ou senha invalidos.");

            if (!string.Equals(empresaRegistrada, "S", StringComparison.OrdinalIgnoreCase) ||
                !string.Equals(usuarioConfirmado, "S", StringComparison.OrdinalIgnoreCase))
            {
                return await MontarRespostaAsync(connection, empresaId, usuarioId, string.Empty, null);
            }

            await AtualizarUltimoLoginAsync(connection, usuarioId);
            return await CriarSessaoAsync(connection, empresaId, usuarioId);
        }

        public async Task<RecuperarSenhaResponse> RecuperarSenhaAsync(RecuperarSenhaRequest request)
        {
            if (request == null)
                throw new ArgumentException("Informe os dados para recuperacao de senha.");

            var email = NormalizarLogin(request.Email);

            if (string.IsNullOrWhiteSpace(email) || email.IndexOf('@') <= 0)
                throw new ArgumentException("E-mail valido e obrigatorio.");

            await using var connection = new MySqlConnection(_connectionString);
            await connection.OpenAsync();
            await GarantirTabelasAsync(connection);

            const string sql = @"
                SELECT u.ID, u.EMAIL
                  FROM RET_USUARIO u
                  JOIN EMPRESA e ON e.ID = u.ID_EMPRESA
                 WHERE LOWER(u.EMAIL) = @email
                   AND u.ATIVO = 'S'
                   AND COALESCE(e.REGISTRADO, 'S') = 'S'
                   AND COALESCE(u.CONFIRMADO, 'S') = 'S'
                 LIMIT 1";

            await using var localizar = new MySqlCommand(sql, connection);
            localizar.Parameters.AddWithValue("@email", email);

            await using var reader = await localizar.ExecuteReaderAsync();
            if (!await reader.ReadAsync())
            {
                return new RecuperarSenhaResponse
                {
                    Sucesso = true,
                    SenhaAlterada = false,
                    Mensagem = "Se o usuario estiver cadastrado, as instrucoes de recuperacao serao disponibilizadas."
                };
            }

            var usuarioId = Convert.ToInt32(reader["ID"]);
            await reader.CloseAsync();

            var senhaTemporaria = CriarSenhaTemporaria();
            var salt = CriarTokenSeguro(24);
            var senhaHash = HashSenha(senhaTemporaria, salt);

            await using var atualizar = new MySqlCommand(@"
                UPDATE RET_USUARIO
                   SET SENHA_HASH = @senhaHash,
                       SENHA_SALT = @salt
                 WHERE ID = @usuarioId", connection);
            atualizar.Parameters.AddWithValue("@senhaHash", senhaHash);
            atualizar.Parameters.AddWithValue("@salt", salt);
            atualizar.Parameters.AddWithValue("@usuarioId", usuarioId);
            await atualizar.ExecuteNonQueryAsync();

            var retornarSenha = DeveRetornarSenhaTemporaria();
            return new RecuperarSenhaResponse
            {
                Sucesso = true,
                SenhaAlterada = true,
                Mensagem = retornarSenha
                    ? "Senha temporaria gerada para teste local. Use-a no proximo login e altere depois."
                    : "Senha temporaria gerada. Consulte o canal configurado pela retaguarda.",
                SenhaTemporaria = retornarSenha ? senhaTemporaria : null
            };
        }

        public async Task<AuthResponse> ValidarTokenAsync(string token)
        {
            if (string.IsNullOrWhiteSpace(token))
                throw new UnauthorizedAccessException("Token nao informado.");

            await using var connection = new MySqlConnection(_connectionString);
            await connection.OpenAsync();
            await GarantirTabelasAsync(connection);

            var tokenHash = HashToken(token);
            const string sql = @"
                SELECT u.ID AS UsuarioId, u.ID_EMPRESA AS EmpresaId
                  FROM RET_SESSAO s
                  JOIN RET_USUARIO u ON u.ID = s.ID_USUARIO
                  JOIN EMPRESA e ON e.ID = u.ID_EMPRESA
                 WHERE s.TOKEN_HASH = @tokenHash
                   AND s.REVOGADO = 'N'
                   AND s.EXPIRA_EM > UTC_TIMESTAMP()
                   AND u.ATIVO = 'S'
                   AND COALESCE(u.CONFIRMADO, 'S') = 'S'
                   AND COALESCE(e.REGISTRADO, 'S') = 'S'
                  LIMIT 1";

            await using var command = new MySqlCommand(sql, connection);
            command.Parameters.AddWithValue("@tokenHash", tokenHash);
            await using var reader = await command.ExecuteReaderAsync();
            if (!await reader.ReadAsync())
                throw new UnauthorizedAccessException("Token invalido ou expirado.");

            var usuarioId = Convert.ToInt32(reader["UsuarioId"]);
            var empresaId = Convert.ToInt32(reader["EmpresaId"]);
            await reader.CloseAsync();

            return await MontarRespostaAsync(connection, empresaId, usuarioId, token, null);
        }

        private static void ValidarCriacao(CriarContaRequest request)
        {
            if (request == null)
                throw new ArgumentException("Informe os dados da conta.");

            if (!CnpjUtils.IsValido(request.Cnpj))
                throw new ArgumentException("CNPJ invalido.");

            if (string.IsNullOrWhiteSpace(request.Email) || request.Email.IndexOf('@') <= 0)
                throw new ArgumentException("E-mail valido e obrigatorio.");

            if (string.IsNullOrWhiteSpace(request.Senha) || request.Senha.Trim().Length < 4)
                throw new ArgumentException("Senha deve possuir pelo menos 4 caracteres.");
        }

        private static async Task GarantirTabelasAsync(MySqlConnection connection)
        {
            var sql = @"
                CREATE TABLE IF NOT EXISTS EMPRESA (
                    ID INT UNSIGNED NOT NULL AUTO_INCREMENT,
                    RAZAO_SOCIAL VARCHAR(150) NULL,
                    NOME_FANTASIA VARCHAR(150) NULL,
                    CNPJ VARCHAR(14) NULL,
                    INSCRICAO_ESTADUAL VARCHAR(30) NULL,
                    INSCRICAO_MUNICIPAL VARCHAR(30) NULL,
                    TIPO_REGIME CHAR(1) NULL,
                    CRT CHAR(1) NULL,
                    DATA_CONSTITUICAO DATE NULL,
                    TIPO CHAR(1) NULL,
                    EMAIL VARCHAR(250) NULL,
                    LOGRADOURO VARCHAR(250) NULL,
                    NUMERO VARCHAR(10) NULL,
                    COMPLEMENTO VARCHAR(100) NULL,
                    CEP VARCHAR(8) NULL,
                    BAIRRO VARCHAR(100) NULL,
                    CIDADE VARCHAR(100) NULL,
                    UF CHAR(2) NULL,
                    FONE VARCHAR(15) NULL,
                    CONTATO VARCHAR(30) NULL,
                    CODIGO_IBGE_CIDADE INT UNSIGNED NULL,
                    CODIGO_IBGE_UF INT UNSIGNED NULL,
                    LOGOTIPO TEXT NULL,
                    REGISTRADO CHAR(1) NULL,
                    NATUREZA_JURIDICA VARCHAR(200) NULL,
                    SIMEI CHAR(1) NULL,
                    EMAIL_PAGAMENTO VARCHAR(250) NULL,
                    DATA_REGISTRO DATE NULL,
                    HORA_REGISTRO VARCHAR(8) NULL,
                    PRIMARY KEY (ID),
                    UNIQUE KEY UK_EMPRESA_CNPJ (CNPJ)
                ) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4;

                CREATE TABLE IF NOT EXISTS RET_USUARIO (
                    ID INT UNSIGNED NOT NULL AUTO_INCREMENT,
                    ID_EMPRESA INT UNSIGNED NOT NULL,
                    NOME VARCHAR(150) NULL,
                    LOGIN VARCHAR(80) NOT NULL,
                    EMAIL VARCHAR(180) NULL,
                    SENHA_HASH VARCHAR(128) NOT NULL,
                    SENHA_SALT VARCHAR(64) NOT NULL,
                    PERFIL VARCHAR(30) NOT NULL,
                    CONFIRMADO CHAR(1) NOT NULL DEFAULT 'S',
                    CONFIRMADO_EM DATETIME NULL,
                    ATIVO CHAR(1) NOT NULL DEFAULT 'S',
                    CRIADO_EM DATETIME NOT NULL DEFAULT CURRENT_TIMESTAMP,
                    ULTIMO_LOGIN DATETIME NULL,
                    PRIMARY KEY (ID),
                    UNIQUE KEY UK_RET_USUARIO_EMPRESA_LOGIN (ID_EMPRESA, LOGIN),
                    CONSTRAINT FK_RET_USUARIO_EMPRESA FOREIGN KEY (ID_EMPRESA) REFERENCES EMPRESA(ID)
                ) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4;

                CREATE TABLE IF NOT EXISTS RET_SESSAO (
                    ID INT UNSIGNED NOT NULL AUTO_INCREMENT,
                    ID_USUARIO INT UNSIGNED NOT NULL,
                    TOKEN_HASH VARCHAR(128) NOT NULL,
                    CRIADO_EM DATETIME NOT NULL DEFAULT CURRENT_TIMESTAMP,
                    EXPIRA_EM DATETIME NOT NULL,
                    REVOGADO CHAR(1) NOT NULL DEFAULT 'N',
                    PRIMARY KEY (ID),
                    UNIQUE KEY UK_RET_SESSAO_TOKEN (TOKEN_HASH),
                    CONSTRAINT FK_RET_SESSAO_USUARIO FOREIGN KEY (ID_USUARIO) REFERENCES RET_USUARIO(ID)
                ) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4;";

            await using var command = new MySqlCommand(sql, connection);
            await command.ExecuteNonQueryAsync();
            await GarantirColunasUsuarioAsync(connection);
            await GarantirColunasSessaoAsync(connection);
        }

        private static async Task GarantirColunasUsuarioAsync(MySqlConnection connection)
        {
            if (!await ColunaExisteAsync(connection, "RET_USUARIO", "EMAIL"))
                await ExecutarAsync(connection, "ALTER TABLE RET_USUARIO ADD COLUMN EMAIL VARCHAR(180) NULL AFTER LOGIN");

            if (!await ColunaExisteAsync(connection, "RET_USUARIO", "CONFIRMADO"))
                await ExecutarAsync(connection, "ALTER TABLE RET_USUARIO ADD COLUMN CONFIRMADO CHAR(1) NOT NULL DEFAULT 'S' AFTER PERFIL");

            if (!await ColunaExisteAsync(connection, "RET_USUARIO", "CONFIRMADO_EM"))
                await ExecutarAsync(connection, "ALTER TABLE RET_USUARIO ADD COLUMN CONFIRMADO_EM DATETIME NULL AFTER CONFIRMADO");

            if (!await IndiceExisteAsync(connection, "RET_USUARIO", "UK_RET_USUARIO_EMAIL"))
            {
                try
                {
                    await ExecutarAsync(connection, "ALTER TABLE RET_USUARIO ADD UNIQUE INDEX UK_RET_USUARIO_EMAIL (EMAIL)");
                }
                catch (MySqlException ex) when (ex.Number == 1062 || ex.Number == 1061)
                {
                    // Existem emails duplicados — índice não criado até os dados serem corrigidos
                }
            }
        }

        private static async Task<bool> IndiceExisteAsync(MySqlConnection connection, string tabela, string indice)
        {
            await using var command = new MySqlCommand(@"
                SELECT COUNT(*)
                  FROM INFORMATION_SCHEMA.STATISTICS
                 WHERE TABLE_SCHEMA = DATABASE()
                   AND TABLE_NAME = @tabela
                   AND INDEX_NAME = @indice", connection);
            command.Parameters.AddWithValue("@tabela", tabela);
            command.Parameters.AddWithValue("@indice", indice);
            return Convert.ToInt32(await command.ExecuteScalarAsync()) > 0;
        }

        private static async Task GarantirColunasSessaoAsync(MySqlConnection connection)
        {
            if (!await ColunaExisteAsync(connection, "RET_SESSAO", "EXPIRA_EM"))
                await ExecutarAsync(connection, "ALTER TABLE RET_SESSAO ADD COLUMN EXPIRA_EM DATETIME NOT NULL DEFAULT (UTC_TIMESTAMP())");

            if (!await ColunaExisteAsync(connection, "RET_SESSAO", "REVOGADO"))
                await ExecutarAsync(connection, "ALTER TABLE RET_SESSAO ADD COLUMN REVOGADO CHAR(1) NOT NULL DEFAULT 'N'");

            if (await ColunaExisteAsync(connection, "RET_SESSAO", "DATA_EXPIRACAO"))
                await ExecutarAsync(connection, "UPDATE RET_SESSAO SET EXPIRA_EM = DATA_EXPIRACAO WHERE DATA_EXPIRACAO IS NOT NULL");

            if (await ColunaExisteAsync(connection, "RET_SESSAO", "ATIVO"))
                await ExecutarAsync(connection, "UPDATE RET_SESSAO SET REVOGADO = CASE WHEN ATIVO = 'S' THEN 'N' ELSE 'S' END");
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

        private static async Task ExecutarAsync(MySqlConnection connection, string sql)
        {
            await using var command = new MySqlCommand(sql, connection);
            await command.ExecuteNonQueryAsync();
        }

        private static async Task<int?> ObterEmpresaIdAsync(MySqlConnection connection, string cnpj)
        {
            await using var command = new MySqlCommand("SELECT ID FROM EMPRESA WHERE CNPJ = @cnpj LIMIT 1", connection);
            command.Parameters.AddWithValue("@cnpj", cnpj);
            var result = await command.ExecuteScalarAsync();
            return result == null || result == DBNull.Value ? null : Convert.ToInt32(result);
        }

        private static async Task<int> InserirEmpresaAsync(MySqlConnection connection, string cnpj, CriarContaRequest request)
        {
            const string sql = @"
                INSERT INTO EMPRESA
                    (RAZAO_SOCIAL, NOME_FANTASIA, CNPJ, EMAIL, REGISTRADO, DATA_REGISTRO, HORA_REGISTRO)
                VALUES
                    (@razaoSocial, @nomeFantasia, @cnpj, @email, 'P', NULL, NULL);
                SELECT LAST_INSERT_ID();";

            await using var command = new MySqlCommand(sql, connection);
            command.Parameters.AddWithValue("@razaoSocial", ValorOuPadrao(request.RazaoSocial, request.NomeFantasia, "Empresa PDV"));
            command.Parameters.AddWithValue("@nomeFantasia", ValorOuPadrao(request.NomeFantasia, request.RazaoSocial, "Empresa PDV"));
            command.Parameters.AddWithValue("@cnpj", cnpj);
            command.Parameters.AddWithValue("@email", (object)request.Email ?? DBNull.Value);
            return Convert.ToInt32(await command.ExecuteScalarAsync());
        }

        private static async Task<int?> ObterUsuarioIdAsync(MySqlConnection connection, int empresaId, string login)
        {
            await using var command = new MySqlCommand("SELECT ID FROM RET_USUARIO WHERE ID_EMPRESA = @empresaId AND LOWER(LOGIN) = @login LIMIT 1", connection);
            command.Parameters.AddWithValue("@empresaId", empresaId);
            command.Parameters.AddWithValue("@login", login);
            var result = await command.ExecuteScalarAsync();
            return result == null || result == DBNull.Value ? null : Convert.ToInt32(result);
        }

        private static async Task<int?> ObterUsuarioIdPorEmailAsync(MySqlConnection connection, string email)
        {
            await using var command = new MySqlCommand("SELECT ID FROM RET_USUARIO WHERE LOWER(EMAIL) = @email LIMIT 1", connection);
            command.Parameters.AddWithValue("@email", email);
            var result = await command.ExecuteScalarAsync();
            return result == null || result == DBNull.Value ? null : Convert.ToInt32(result);
        }

        private static async Task<string> ObterUsuarioConfirmadoAsync(MySqlConnection connection, int usuarioId)
        {
            await using var command = new MySqlCommand("SELECT COALESCE(CONFIRMADO, 'S') FROM RET_USUARIO WHERE ID = @id LIMIT 1", connection);
            command.Parameters.AddWithValue("@id", usuarioId);
            return (await command.ExecuteScalarAsync())?.ToString() ?? "S";
        }

        private static async Task AtualizarUsuarioPendenteAsync(MySqlConnection connection, int usuarioId, string nome, string email, string perfil)
        {
            await using var command = new MySqlCommand(@"
                UPDATE RET_USUARIO
                   SET NOME = @nome,
                       EMAIL = @email,
                       PERFIL = @perfil
                 WHERE ID = @usuarioId
                   AND COALESCE(CONFIRMADO, 'S') <> 'S'", connection);
            command.Parameters.AddWithValue("@nome", ValorOuPadrao(nome, "Usuario"));
            command.Parameters.AddWithValue("@email", string.IsNullOrWhiteSpace(email) ? DBNull.Value : (object)email);
            command.Parameters.AddWithValue("@perfil", perfil);
            command.Parameters.AddWithValue("@usuarioId", usuarioId);
            await command.ExecuteNonQueryAsync();
        }

        private static async Task<int> InserirUsuarioAsync(MySqlConnection connection, int empresaId, string nome, string login, string senhaHash, string salt, string perfil, string email)
        {
            const string sql = @"
                INSERT INTO RET_USUARIO
                    (ID_EMPRESA, NOME, LOGIN, EMAIL, SENHA_HASH, SENHA_SALT, PERFIL, CONFIRMADO, ATIVO)
                VALUES
                    (@empresaId, @nome, @login, @email, @senhaHash, @salt, @perfil, 'P', 'S');
                SELECT LAST_INSERT_ID();";

            await using var command = new MySqlCommand(sql, connection);
            command.Parameters.AddWithValue("@empresaId", empresaId);
            command.Parameters.AddWithValue("@nome", ValorOuPadrao(nome, login, "Administrador"));
            command.Parameters.AddWithValue("@login", login);
            command.Parameters.AddWithValue("@email", string.IsNullOrWhiteSpace(email) ? DBNull.Value : (object)email);
            command.Parameters.AddWithValue("@senhaHash", senhaHash);
            command.Parameters.AddWithValue("@salt", salt);
            command.Parameters.AddWithValue("@perfil", perfil);
            return Convert.ToInt32(await command.ExecuteScalarAsync());
        }

        private static async Task AtualizarUltimoLoginAsync(MySqlConnection connection, int usuarioId)
        {
            await using var command = new MySqlCommand("UPDATE RET_USUARIO SET ULTIMO_LOGIN = UTC_TIMESTAMP() WHERE ID = @id", connection);
            command.Parameters.AddWithValue("@id", usuarioId);
            await command.ExecuteNonQueryAsync();
        }

        private static async Task<AuthResponse> CriarSessaoAsync(MySqlConnection connection, int empresaId, int usuarioId)
        {
            var token = CriarTokenSeguro(48);
            var expiraEm = DateTime.UtcNow.AddHours(12);
            var tokenHash = HashToken(token);

            await using var command = new MySqlCommand(@"
                INSERT INTO RET_SESSAO (ID_USUARIO, TOKEN_HASH, EXPIRA_EM, REVOGADO)
                VALUES (@usuarioId, @tokenHash, @expiraEm, 'N')", connection);
            command.Parameters.AddWithValue("@usuarioId", usuarioId);
            command.Parameters.AddWithValue("@tokenHash", tokenHash);
            command.Parameters.AddWithValue("@expiraEm", expiraEm);
            await command.ExecuteNonQueryAsync();

            return await MontarRespostaAsync(connection, empresaId, usuarioId, token, expiraEm);
        }

        private static async Task<AuthResponse> MontarRespostaAsync(MySqlConnection connection, int empresaId, int usuarioId, string token, DateTime? expiraEm)
        {
            const string sql = @"
                SELECT
                    u.ID AS UsuarioId, u.NOME, u.LOGIN, u.EMAIL, u.PERFIL, COALESCE(u.CONFIRMADO, 'S') AS CONFIRMADO,
                    e.ID AS EmpresaId, e.CNPJ, e.RAZAO_SOCIAL, e.NOME_FANTASIA, e.REGISTRADO
                FROM RET_USUARIO u
                JOIN EMPRESA e ON e.ID = u.ID_EMPRESA
                WHERE u.ID = @usuarioId AND e.ID = @empresaId";

            await using var command = new MySqlCommand(sql, connection);
            command.Parameters.AddWithValue("@usuarioId", usuarioId);
            command.Parameters.AddWithValue("@empresaId", empresaId);
            await using var reader = await command.ExecuteReaderAsync();
            if (!await reader.ReadAsync())
                throw new InvalidOperationException("Sessao criada, mas usuario/empresa nao foram localizados.");

            var cnpj = reader["CNPJ"].ToString();
            return new AuthResponse
            {
                Token = token,
                ExpiraEm = expiraEm ?? DateTime.UtcNow.AddHours(12),
                Usuario = new AuthUsuarioResponse
                {
                    Id = Convert.ToInt32(reader["UsuarioId"]),
                    Nome = reader["NOME"].ToString(),
                    Login = reader["LOGIN"].ToString(),
                    Perfil = reader["PERFIL"].ToString(),
                    Email = reader["EMAIL"] == DBNull.Value ? string.Empty : reader["EMAIL"].ToString(),
                    Confirmado = reader["CONFIRMADO"].ToString()
                },
                Empresa = new AuthEmpresaResponse
                {
                    Id = Convert.ToInt32(reader["EmpresaId"]),
                    Cnpj = cnpj,
                    RazaoSocial = reader["RAZAO_SOCIAL"].ToString(),
                    NomeFantasia = reader["NOME_FANTASIA"].ToString(),
                    Registrado = reader["REGISTRADO"].ToString(),
                    BancoOperacional = NomeBancoOperacional(cnpj)
                }
            };
        }

        public static string NomeBancoOperacional(string cnpj)
        {
            var overrideDatabase = Environment.GetEnvironmentVariable("PDV_OPERACIONAL_DATABASE");
            if (!string.IsNullOrWhiteSpace(overrideDatabase))
                return overrideDatabase.Trim();

            return "pdv_operacional";
        }

        private static string NormalizarLogin(string login)
        {
            return (login ?? string.Empty).Trim().ToLowerInvariant();
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



        private static string CriarTokenSeguro(int bytes)
        {
            var buffer = RandomNumberGenerator.GetBytes(bytes);
            return Convert.ToBase64String(buffer)
                .Replace("+", "-")
                .Replace("/", "_")
                .TrimEnd('=');
        }

        private static string CriarSenhaTemporaria()
        {
            return "Tmp@" + CriarTokenSeguro(9).Replace("-", "").Replace("_", "").Substring(0, 8);
        }

        private static bool DeveRetornarSenhaTemporaria()
        {
            var flag = Environment.GetEnvironmentVariable("RETORNA_SENHA_TEMPORARIA");
            var ambiente = Environment.GetEnvironmentVariable("ASPNETCORE_ENVIRONMENT");

            return string.Equals(flag, "true", StringComparison.OrdinalIgnoreCase)
                || string.Equals(flag, "1", StringComparison.OrdinalIgnoreCase)
                || string.Equals(ambiente, "Development", StringComparison.OrdinalIgnoreCase);
        }

        private static string HashSenha(string senha, string salt)
        {
            return HashToken($"{salt}:{senha}");
        }

        private static string HashToken(string token)
        {
            var bytes = SHA256.HashData(Encoding.UTF8.GetBytes(token));
            return Convert.ToHexString(bytes).ToLowerInvariant();
        }
    }
}
