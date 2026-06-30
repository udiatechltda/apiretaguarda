using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Hosting;
using MySql.Data.MySqlClient;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Threading.Tasks;
using T2TiRetaguardaSH.Models.Produtos;
using T2TiRetaguardaSH.Util;

namespace T2TiRetaguardaSH.Services.Produtos
{
    public class ProdutoImagemService
    {
        private static readonly HashSet<string> ExtensoesPermitidas = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            ".jpg",
            ".jpeg",
            ".png"
        };

        private static readonly HashSet<string> ContentTypesPermitidos = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "image/jpeg",
            "image/png"
        };

        private readonly string _adminConnectionString;
        private readonly string _operacionalDatabase;
        private readonly string _rootPath;
        private readonly string _publicBaseUrl;
        private readonly long _maxBytes;

        public ProdutoImagemService(IConfiguration configuration, IHostEnvironment environment)
        {
            _adminConnectionString = configuration.GetConnectionString("AdminConnection")
                ?? Biblioteca.Config?.GetConnectionString("AdminConnection")
                ?? RemoverDatabase(configuration.GetConnectionString("DefaultConnection") ?? Biblioteca.Config?.GetConnectionString("DefaultConnection"))
                ?? "Server=localhost;Port=3306;Uid=root;Pwd=MySql@2025;";

            _operacionalDatabase = Environment.GetEnvironmentVariable("PDV_OPERACIONAL_DATABASE")
                ?? configuration.GetValue<string>("PdvOperacionalDatabase")
                ?? "pdv_operacional";

            var configuredRoot = Environment.GetEnvironmentVariable("PRODUTO_IMAGENS_ROOT_PATH")
                ?? configuration.GetValue<string>("ProdutoImagens:RootPath");
            _rootPath = string.IsNullOrWhiteSpace(configuredRoot)
                ? ObterRootPathPadraoServidor()
                : Path.GetFullPath(configuredRoot);

            _publicBaseUrl = (configuration.GetValue<string>("ProdutoImagens:PublicBaseUrl") ?? string.Empty).Trim().TrimEnd('/');
            _maxBytes = configuration.GetValue<long?>("ProdutoImagens:MaxBytes") ?? 5 * 1024 * 1024;
        }

        public async Task<ProdutoImagemUploadResponse> SalvarImagemAsync(int empresaId, string cnpj, int produtoId, IFormFile arquivo, HttpRequest request)
        {
            if (empresaId <= 0)
                throw new ArgumentException("Empresa invalida para upload de imagem.");

            cnpj = ApenasDigitos(cnpj);
            if (cnpj.Length != 14)
                throw new ArgumentException("CNPJ invalido para upload de imagem.");

            if (produtoId <= 0)
                throw new ArgumentException("Produto invalido para upload de imagem.");

            if (arquivo == null || arquivo.Length == 0)
                throw new ArgumentException("Arquivo de imagem nao informado.");

            if (arquivo.Length > _maxBytes)
                throw new ArgumentException("Imagem acima do tamanho maximo permitido.");

            var extensao = Path.GetExtension(arquivo.FileName);
            if (!ExtensoesPermitidas.Contains(extensao))
                throw new ArgumentException("Formato de imagem invalido. Use JPG ou PNG.");

            var contentType = string.IsNullOrWhiteSpace(arquivo.ContentType) ? "application/octet-stream" : arquivo.ContentType;
            if (!ContentTypesPermitidos.Contains(contentType))
                throw new ArgumentException("Tipo de conteudo invalido. Use JPG ou PNG.");

            var empresaDir = Path.Combine(_rootPath, "empresas", cnpj, "produtos");
            Directory.CreateDirectory(empresaDir);

            var nomeArquivo = $"{Guid.NewGuid():N}{extensao.ToLowerInvariant()}";
            var caminhoAbsoluto = Path.Combine(empresaDir, nomeArquivo);
            var hash = await SalvarArquivoECalcularHashAsync(arquivo, caminhoAbsoluto);
            var caminhoRelativo = Path.Combine("empresas", cnpj, "produtos", nomeArquivo).Replace(Path.DirectorySeparatorChar, '/');
            var atualizadoEm = DateTime.UtcNow;
            var url = MontarUrl(request, produtoId);

            await using var connection = new MySqlConnection(_adminConnectionString);
            await connection.OpenAsync();
            await GarantirBancoEstruturaAsync(connection);
            await ExecutarAsync(connection, $"USE `{_operacionalDatabase}`");

            await using var transaction = await connection.BeginTransactionAsync();
            try
            {
                await MarcarImagensAnterioresExcluidasAsync(connection, (MySqlTransaction)transaction, empresaId, produtoId, atualizadoEm);
                await InserirMetadadosAsync(
                    connection,
                    (MySqlTransaction)transaction,
                    empresaId,
                    cnpj,
                    produtoId,
                    url,
                    caminhoRelativo,
                    nomeArquivo,
                    contentType,
                    hash,
                    arquivo.Length,
                    atualizadoEm);

                await transaction.CommitAsync();
            }
            catch
            {
                await transaction.RollbackAsync();
                if (File.Exists(caminhoAbsoluto))
                    File.Delete(caminhoAbsoluto);
                throw;
            }

            return new ProdutoImagemUploadResponse
            {
                ProdutoId = produtoId,
                Url = url,
                CaminhoRelativo = caminhoRelativo,
                Hash = hash,
                TamanhoBytes = arquivo.Length,
                ContentType = contentType,
                AtualizadoEm = atualizadoEm
            };
        }

        public async Task<ProdutoImagemManifestResponse> ObterManifestAsync(int empresaId, string cnpj, HttpRequest request)
        {
            await using var connection = new MySqlConnection(_adminConnectionString);
            await connection.OpenAsync();
            await GarantirBancoEstruturaAsync(connection);
            await ExecutarAsync(connection, $"USE `{_operacionalDatabase}`");

            var response = new ProdutoImagemManifestResponse
            {
                Cnpj = ApenasDigitos(cnpj)
            };

            await using var command = new MySqlCommand(@"
                SELECT img.ID_PRODUTO, img.URL, img.CAMINHO_RELATIVO, img.HASH_SHA256, img.TAMANHO_BYTES, img.CONTENT_TYPE, img.ATUALIZADO_EM, img.EXCLUIDO
                  FROM PDV_PRODUTO_IMAGEM_ARQUIVO img
                  JOIN (
                        SELECT ID_PRODUTO, MAX(ID) AS ID
                          FROM PDV_PRODUTO_IMAGEM_ARQUIVO
                         WHERE ID_EMPRESA = @empresaId
                         GROUP BY ID_PRODUTO
                       ) ult ON ult.ID = img.ID
                 WHERE img.ID_EMPRESA = @empresaId
                 ORDER BY img.ID_PRODUTO", connection);
            command.Parameters.AddWithValue("@empresaId", empresaId);

            await using var reader = await command.ExecuteReaderAsync();
            while (await reader.ReadAsync())
            {
                var produtoId = Convert.ToInt32(reader["ID_PRODUTO"]);
                response.Imagens.Add(new ProdutoImagemManifestItem
                {
                    ProdutoId = produtoId,
                    Url = string.IsNullOrWhiteSpace(reader["URL"]?.ToString()) ? MontarUrl(request, produtoId) : reader["URL"].ToString(),
                    CaminhoRelativo = reader["CAMINHO_RELATIVO"]?.ToString(),
                    Hash = reader["HASH_SHA256"]?.ToString(),
                    TamanhoBytes = Convert.ToInt64(reader["TAMANHO_BYTES"]),
                    ContentType = reader["CONTENT_TYPE"]?.ToString(),
                    AtualizadoEm = Convert.ToDateTime(reader["ATUALIZADO_EM"]),
                    Excluido = string.Equals(reader["EXCLUIDO"]?.ToString(), "S", StringComparison.OrdinalIgnoreCase)
                });
            }

            return response;
        }

        public async Task<ProdutoImagemArquivo> ObterArquivoAsync(int empresaId, int produtoId)
        {
            if (produtoId <= 0)
                throw new ArgumentException("Produto invalido para download de imagem.");

            await using var connection = new MySqlConnection(_adminConnectionString);
            await connection.OpenAsync();
            await GarantirBancoEstruturaAsync(connection);
            await ExecutarAsync(connection, $"USE `{_operacionalDatabase}`");

            await using var command = new MySqlCommand(@"
                SELECT CAMINHO_RELATIVO, NOME_ARQUIVO, CONTENT_TYPE
                  FROM PDV_PRODUTO_IMAGEM_ARQUIVO
                 WHERE ID_EMPRESA = @empresaId
                   AND ID_PRODUTO = @produtoId
                   AND EXCLUIDO = 'N'
                 ORDER BY ATUALIZADO_EM DESC, ID DESC
                 LIMIT 1", connection);
            command.Parameters.AddWithValue("@empresaId", empresaId);
            command.Parameters.AddWithValue("@produtoId", produtoId);

            await using var reader = await command.ExecuteReaderAsync();
            if (!await reader.ReadAsync())
                return null;

            var caminhoRelativo = reader["CAMINHO_RELATIVO"]?.ToString();
            var caminho = ResolverCaminhoFisico(caminhoRelativo);
            if (string.IsNullOrWhiteSpace(caminho) || !File.Exists(caminho))
                return null;

            return new ProdutoImagemArquivo
            {
                Caminho = caminho,
                NomeArquivo = reader["NOME_ARQUIVO"]?.ToString(),
                ContentType = reader["CONTENT_TYPE"]?.ToString()
            };
        }

        public async Task RemoverImagemAsync(int empresaId, int produtoId)
        {
            if (produtoId <= 0)
                throw new ArgumentException("Produto invalido para remover imagem.");

            await using var connection = new MySqlConnection(_adminConnectionString);
            await connection.OpenAsync();
            await GarantirBancoEstruturaAsync(connection);
            await ExecutarAsync(connection, $"USE `{_operacionalDatabase}`");

            await using var command = new MySqlCommand(@"
                UPDATE PDV_PRODUTO_IMAGEM_ARQUIVO
                   SET EXCLUIDO = 'S',
                       ATUALIZADO_EM = UTC_TIMESTAMP()
                 WHERE ID_EMPRESA = @empresaId
                   AND ID_PRODUTO = @produtoId
                   AND EXCLUIDO = 'N'", connection);
            command.Parameters.AddWithValue("@empresaId", empresaId);
            command.Parameters.AddWithValue("@produtoId", produtoId);
            await command.ExecuteNonQueryAsync();
        }

        private async Task GarantirBancoEstruturaAsync(MySqlConnection connection)
        {
            await ExecutarAsync(connection, $"CREATE DATABASE IF NOT EXISTS `{_operacionalDatabase}` DEFAULT CHARACTER SET utf8mb4 COLLATE utf8mb4_unicode_ci");
            await ExecutarAsync(connection, $"USE `{_operacionalDatabase}`");
            await ExecutarAsync(connection, @"
                CREATE TABLE IF NOT EXISTS PDV_PRODUTO_IMAGEM_ARQUIVO (
                    ID BIGINT UNSIGNED NOT NULL AUTO_INCREMENT,
                    ID_EMPRESA INT UNSIGNED NOT NULL,
                    CNPJ VARCHAR(14) NOT NULL,
                    ID_PRODUTO INT NOT NULL,
                    URL VARCHAR(1000) NOT NULL,
                    CAMINHO_RELATIVO VARCHAR(1000) NOT NULL,
                    NOME_ARQUIVO VARCHAR(255) NOT NULL,
                    CONTENT_TYPE VARCHAR(100) NOT NULL,
                    HASH_SHA256 VARCHAR(64) NOT NULL,
                    TAMANHO_BYTES BIGINT NOT NULL,
                    ATUALIZADO_EM DATETIME NOT NULL,
                    EXCLUIDO CHAR(1) NOT NULL DEFAULT 'N',
                    PRIMARY KEY (ID),
                    INDEX IX_PDV_PRODUTO_IMAGEM_EMPRESA_PRODUTO (ID_EMPRESA, ID_PRODUTO),
                    INDEX IX_PDV_PRODUTO_IMAGEM_HASH (HASH_SHA256)
                ) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4 COLLATE=utf8mb4_unicode_ci");
        }

        private static async Task InserirMetadadosAsync(
            MySqlConnection connection,
            MySqlTransaction transaction,
            int empresaId,
            string cnpj,
            int produtoId,
            string url,
            string caminhoRelativo,
            string nomeArquivo,
            string contentType,
            string hash,
            long tamanhoBytes,
            DateTime atualizadoEm)
        {
            await using var command = new MySqlCommand(@"
                INSERT INTO PDV_PRODUTO_IMAGEM_ARQUIVO
                    (ID_EMPRESA, CNPJ, ID_PRODUTO, URL, CAMINHO_RELATIVO, NOME_ARQUIVO, CONTENT_TYPE, HASH_SHA256, TAMANHO_BYTES, ATUALIZADO_EM, EXCLUIDO)
                VALUES
                    (@empresaId, @cnpj, @produtoId, @url, @caminhoRelativo, @nomeArquivo, @contentType, @hash, @tamanhoBytes, @atualizadoEm, 'N')", connection, transaction);
            command.Parameters.AddWithValue("@empresaId", empresaId);
            command.Parameters.AddWithValue("@cnpj", cnpj);
            command.Parameters.AddWithValue("@produtoId", produtoId);
            command.Parameters.AddWithValue("@url", url);
            command.Parameters.AddWithValue("@caminhoRelativo", caminhoRelativo);
            command.Parameters.AddWithValue("@nomeArquivo", nomeArquivo);
            command.Parameters.AddWithValue("@contentType", contentType);
            command.Parameters.AddWithValue("@hash", hash);
            command.Parameters.AddWithValue("@tamanhoBytes", tamanhoBytes);
            command.Parameters.AddWithValue("@atualizadoEm", atualizadoEm);
            await command.ExecuteNonQueryAsync();
        }

        private static async Task MarcarImagensAnterioresExcluidasAsync(MySqlConnection connection, MySqlTransaction transaction, int empresaId, int produtoId, DateTime atualizadoEm)
        {
            await using var command = new MySqlCommand(@"
                UPDATE PDV_PRODUTO_IMAGEM_ARQUIVO
                   SET EXCLUIDO = 'S',
                       ATUALIZADO_EM = @atualizadoEm
                 WHERE ID_EMPRESA = @empresaId
                   AND ID_PRODUTO = @produtoId
                   AND EXCLUIDO = 'N'", connection, transaction);
            command.Parameters.AddWithValue("@empresaId", empresaId);
            command.Parameters.AddWithValue("@produtoId", produtoId);
            command.Parameters.AddWithValue("@atualizadoEm", atualizadoEm);
            await command.ExecuteNonQueryAsync();
        }

        private static async Task<string> SalvarArquivoECalcularHashAsync(IFormFile arquivo, string caminhoAbsoluto)
        {
            await using var origem = arquivo.OpenReadStream();
            await using var destino = new FileStream(caminhoAbsoluto, FileMode.CreateNew, FileAccess.Write, FileShare.None);
            using var sha256 = SHA256.Create();

            var buffer = new byte[81920];
            int read;
            while ((read = await origem.ReadAsync(buffer, 0, buffer.Length)) > 0)
            {
                await destino.WriteAsync(buffer, 0, read);
                sha256.TransformBlock(buffer, 0, read, null, 0);
            }

            sha256.TransformFinalBlock(Array.Empty<byte>(), 0, 0);
            return BitConverter.ToString(sha256.Hash).Replace("-", "").ToLowerInvariant();
        }

        private string ResolverCaminhoFisico(string caminhoRelativo)
        {
            if (string.IsNullOrWhiteSpace(caminhoRelativo))
                return null;

            var relativoNormalizado = caminhoRelativo
                .Replace('/', Path.DirectorySeparatorChar)
                .Replace('\\', Path.DirectorySeparatorChar);
            var path = Path.GetFullPath(Path.Combine(_rootPath, relativoNormalizado));
            var root = Path.GetFullPath(_rootPath);
            if (!root.EndsWith(Path.DirectorySeparatorChar.ToString(), StringComparison.Ordinal))
                root += Path.DirectorySeparatorChar;

            return path.StartsWith(root, StringComparison.OrdinalIgnoreCase) ? path : null;
        }

        private static string ObterRootPathPadraoServidor()
        {
            if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
                return Path.GetFullPath(@"C:\TechOneStorage\produtos-imagens");

            return Path.GetFullPath("/var/techone/storage/produtos-imagens");
        }

        private string MontarUrl(HttpRequest request, int produtoId)
        {
            if (!string.IsNullOrWhiteSpace(_publicBaseUrl))
                return $"{_publicBaseUrl}/api/produtos/{produtoId}/imagem";

            var scheme = request.Headers["X-Forwarded-Proto"].FirstOrDefault();
            if (string.IsNullOrWhiteSpace(scheme))
                scheme = request.Scheme;

            return $"{scheme}://{request.Host}/api/produtos/{produtoId}/imagem";
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

        private static string RemoverDatabase(string connectionString)
        {
            if (string.IsNullOrWhiteSpace(connectionString))
                return null;

            var builder = new MySqlConnectionStringBuilder(connectionString)
            {
                Database = string.Empty
            };
            return builder.ConnectionString;
        }
    }

    public class ProdutoImagemArquivo
    {
        public string Caminho { get; set; }
        public string NomeArquivo { get; set; }
        public string ContentType { get; set; }
    }
}
