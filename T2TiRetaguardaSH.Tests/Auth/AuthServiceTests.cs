using Microsoft.Extensions.Configuration;
using MySql.Data.MySqlClient;
using T2TiRetaguardaSH.Models.Auth;
using T2TiRetaguardaSH.Services.Auth;
using T2TiRetaguardaSH.Util;
using Testcontainers.MySql;
using Xunit;

namespace T2TiRetaguardaSH.Tests.Auth;

/// <summary>
/// Fixture compartilhada: sobe UM container MySQL para toda a classe de testes.
/// Cada teste usa emails únicos, por isso não há conflito sem necessidade de limpar entre testes.
/// </summary>
public sealed class MySqlFixture : IAsyncLifetime
{
    private readonly MySqlContainer _container = new MySqlBuilder()
        .WithDatabase("retaguarda_sh")
        .WithUsername("test_user")
        .WithPassword("test_pass")
        .Build();

    public string ConnectionString { get; private set; } = null!;

    public async Task InitializeAsync()
    {
        await _container.StartAsync();
        ConnectionString = _container.GetConnectionString();
    }

    public async Task DisposeAsync() => await _container.DisposeAsync();
}

/// <summary>
/// Testes de integração do AuthService — cobre login, criação de conta,
/// recuperação de senha, validação de token e unicidade global de email (multitenant).
/// </summary>
public class AuthServiceTests : IClassFixture<MySqlFixture>
{
    private readonly AuthService _service;
    private readonly string _cs;

    public AuthServiceTests(MySqlFixture fixture)
    {
        _cs = fixture.ConnectionString;
        _service = Build(_cs);
    }

    private static AuthService Build(string connectionString)
    {
        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["ConnectionStrings:DefaultConnection"] = connectionString
            })
            .Build();
        return new AuthService(config);
    }

    // ─────────────────────────────────────────────────────────────────────────────
    // CRIAR CONTA
    // ─────────────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task CriarConta_DadosValidos_RetornaUsuarioComEmailECnpj()
    {
        var req = Conta("valid_create@test.com");
        var result = await _service.CriarContaAsync(req);

        Assert.NotNull(result);
        Assert.Equal(req.Email.ToLower(), result.Usuario.Email.ToLower());
        Assert.Equal(Normaliza(req.Cnpj), result.Empresa.Cnpj);
    }

    [Fact]
    public async Task CriarConta_LoginOmitido_UsaEmailComoLogin()
    {
        var req = Conta("loginvazio@test.com");
        req.Login = "";
        var result = await _service.CriarContaAsync(req);

        Assert.Equal(req.Email.ToLower(), result.Usuario.Login.ToLower());
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("semarroba")]
    [InlineData("@semlocal")]
    [InlineData(null)]
    public async Task CriarConta_EmailInvalido_LancaArgumentException(string? email)
    {
        var req = Conta("irrelevante@test.com");
        req.Email = email!;
        await Assert.ThrowsAsync<ArgumentException>(() => _service.CriarContaAsync(req));
    }

    [Theory]
    [InlineData("123")]
    [InlineData("1234567890123")]
    [InlineData("")]
    [InlineData("abc")]
    [InlineData("ABCD1234EFGH00")]   // DV incorreto
    [InlineData("ABCD1234EFGHAA")]   // DV nao numerico
    [InlineData("00000000000000")]   // tudo zeros
    public async Task CriarConta_CnpjInvalido_LancaArgumentException(string cnpj)
    {
        var req = Conta("cnpjinvalido@test.com");
        req.Cnpj = cnpj;
        await Assert.ThrowsAsync<ArgumentException>(() => _service.CriarContaAsync(req));
    }

    [Fact]
    public async Task CriarConta_CnpjAlfanumerico_Sucesso()
    {
        // ABCD1234EFGH46: base ABCD1234EFGH, DV calculado = 46
        var req = Conta("alfa_cnpj@test.com", "ABCD1234EFGH46");
        var result = await _service.CriarContaAsync(req);

        Assert.NotNull(result);
        Assert.Equal("ABCD1234EFGH46", result.Empresa.Cnpj);
    }

    [Fact]
    public async Task CriarConta_CnpjAlfanumericoFormatado_NormalizaEAceita()
    {
        // PM.0O3.6A7/0001-71 é o CNPJ do ticket Jira PDV-40 — deve ser aceito formatado
        var req = Conta("alfa_formatado@test.com", "PM.0O3.6A7/0001-71");
        var result = await _service.CriarContaAsync(req);

        Assert.Equal("PM0O36A7000171", result.Empresa.Cnpj);
    }

    [Fact]
    public async Task CriarConta_CnpjAlfanumericoMinusculas_NormalizaEAceita()
    {
        var req = Conta("alfa_lower@test.com", "abcd1234efgh46");
        var result = await _service.CriarContaAsync(req);

        Assert.Equal("ABCD1234EFGH46", result.Empresa.Cnpj);
    }

    [Theory]
    [InlineData("")]
    [InlineData("ab")]
    [InlineData("abc")]
    public async Task CriarConta_SenhaCurta_LancaArgumentException(string senha)
    {
        var req = Conta("senhacurta@test.com");
        req.Senha = senha;
        await Assert.ThrowsAsync<ArgumentException>(() => _service.CriarContaAsync(req));
    }

    [Fact]
    public async Task CriarConta_EmailDuplicado_CnpjsDiferentes_LancaInvalidOperationException()
    {
        await _service.CriarContaAsync(Conta("dupA@test.com", "11222333000181"));

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            _service.CriarContaAsync(Conta("dupA@test.com", "99888777000100")));
    }

    [Fact]
    public async Task CriarConta_EmailDuplicado_MesmoCnpj_LancaInvalidOperationException()
    {
        await _service.CriarContaAsync(Conta("dupB@test.com", "11222333000181"));

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            _service.CriarContaAsync(Conta("dupB@test.com", "11222333000181")));
    }

    [Fact]
    public async Task CriarConta_UsuarioJaConfirmado_LancaInvalidOperationException()
    {
        var req = Conta("jaconfirmado@test.com", "11222333000181");
        await _service.CriarContaAsync(req);
        await ConfirmarEmpresaEUsuario(req.Email, Normaliza(req.Cnpj));

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            _service.CriarContaAsync(req));
    }

    [Fact]
    public async Task CriarConta_GarantirTabelas_Idempotente_MultiplasChamadas()
    {
        var s2 = Build(_cs);
        // GarantirTabelasAsync + GarantirColunasUsuarioAsync idempotentes
        await _service.CriarContaAsync(Conta("idem1@test.com", "11222333000181"));
        await s2.CriarContaAsync(Conta("idem2@test.com", "22333444000181"));
    }

    // ─────────────────────────────────────────────────────────────────────────────
    // LOGIN
    // ─────────────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task Login_EmailESenhaCorretos_RetornaTokenValido()
    {
        var req = Conta("login_ok@test.com", "11222333000181");
        await _service.CriarContaAsync(req);
        await ConfirmarEmpresaEUsuario(req.Email, Normaliza(req.Cnpj));

        var result = await _service.LoginAsync(new LoginRequest
        {
            Email = req.Email,
            Senha = req.Senha
        });

        Assert.NotEmpty(result.Token);
        Assert.True(result.ExpiraEm > DateTime.UtcNow);
    }

    [Fact]
    public async Task Login_EmailMaiusculo_CaseInsensitive()
    {
        var req = Conta("case@test.com", "11222333000181");
        await _service.CriarContaAsync(req);
        await ConfirmarEmpresaEUsuario(req.Email, Normaliza(req.Cnpj));

        var result = await _service.LoginAsync(new LoginRequest
        {
            Email = req.Email.ToUpper(),
            Senha = req.Senha
        });

        Assert.NotEmpty(result.Token);
    }

    [Fact]
    public async Task Login_SenhaErrada_LancaUnauthorizedAccessException()
    {
        var req = Conta("errado@test.com", "11222333000181");
        await _service.CriarContaAsync(req);
        await ConfirmarEmpresaEUsuario(req.Email, Normaliza(req.Cnpj));

        await Assert.ThrowsAsync<UnauthorizedAccessException>(() =>
            _service.LoginAsync(new LoginRequest
            {
                Email = req.Email,
                Senha = "SenhaErrada!"
            }));
    }

    [Fact]
    public async Task Login_EmailNaoCadastrado_LancaUnauthorizedAccessException()
    {
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() =>
            _service.LoginAsync(new LoginRequest
            {
                Email = "fantasma@test.com",
                Senha = "Qualquer@1"
            }));
    }

    [Theory]
    [InlineData("", "Senha@123")]
    [InlineData("invalido", "Senha@123")]
    [InlineData("@semlocal", "Senha@123")]
    [InlineData("ok@test.com", "")]
    [InlineData("ok@test.com", "   ")]
    public async Task Login_DadosInvalidos_LancaArgumentException(string email, string senha)
    {
        await Assert.ThrowsAsync<ArgumentException>(() =>
            _service.LoginAsync(new LoginRequest { Email = email, Senha = senha }));
    }

    [Fact]
    public async Task Login_UsuarioInativo_LancaUnauthorizedAccessException()
    {
        var req = Conta("inativo@test.com", "11222333000181");
        await _service.CriarContaAsync(req);
        await DesativarUsuario(req.Email);

        await Assert.ThrowsAsync<UnauthorizedAccessException>(() =>
            _service.LoginAsync(new LoginRequest { Email = req.Email, Senha = req.Senha }));
    }

    [Fact]
    public async Task Login_EmpresaPendente_RetornaSessionSemToken()
    {
        // Recém-criada: REGISTRADO='P', CONFIRMADO='P' → sem token
        var req = Conta("pendente@test.com", "11222333000181");
        await _service.CriarContaAsync(req);

        var result = await _service.LoginAsync(new LoginRequest
        {
            Email = req.Email,
            Senha = req.Senha
        });

        Assert.Empty(result.Token);
    }

    [Fact]
    public async Task Login_RetornaEmpresaCorretaDoUsuario()
    {
        var req = Conta("empresa_check@test.com", "11222333000181");
        await _service.CriarContaAsync(req);
        await ConfirmarEmpresaEUsuario(req.Email, Normaliza(req.Cnpj));

        var result = await _service.LoginAsync(new LoginRequest
        {
            Email = req.Email,
            Senha = req.Senha
        });

        Assert.Equal(Normaliza(req.Cnpj), result.Empresa.Cnpj);
        Assert.Equal(req.Email.ToLower(), result.Usuario.Email.ToLower());
    }

    // ─────────────────────────────────────────────────────────────────────────────
    // RECUPERAR SENHA
    // ─────────────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task RecuperarSenha_EmailCadastrado_RetornaSucessoComAlteracao()
    {
        var req = Conta("recover@test.com", "11222333000181");
        await _service.CriarContaAsync(req);
        await ConfirmarEmpresaEUsuario(req.Email, Normaliza(req.Cnpj));

        var result = await _service.RecuperarSenhaAsync(new RecuperarSenhaRequest
        {
            Email = req.Email
        });

        Assert.True(result.Sucesso);
        Assert.True(result.SenhaAlterada);
    }

    [Fact]
    public async Task RecuperarSenha_EmailNaoCadastrado_RetornaSucessoSemAlterar()
    {
        // Resposta genérica por segurança — não revela se email existe
        var result = await _service.RecuperarSenhaAsync(new RecuperarSenhaRequest
        {
            Email = "naoexiste@test.com"
        });

        Assert.True(result.Sucesso);
        Assert.False(result.SenhaAlterada);
    }

    [Theory]
    [InlineData("")]
    [InlineData("invalido")]
    [InlineData("@semlocal")]
    [InlineData(null)]
    public async Task RecuperarSenha_EmailInvalido_LancaArgumentException(string? email)
    {
        await Assert.ThrowsAsync<ArgumentException>(() =>
            _service.RecuperarSenhaAsync(new RecuperarSenhaRequest { Email = email! }));
    }

    [Fact]
    public async Task RecuperarSenha_SenhaTemporaria_PermiteLoginComNovaSenha()
    {
        var req = Conta("tmpsenha@test.com", "11222333000181");
        await _service.CriarContaAsync(req);
        await ConfirmarEmpresaEUsuario(req.Email, Normaliza(req.Cnpj));

        Environment.SetEnvironmentVariable("RETORNA_SENHA_TEMPORARIA", "true");
        var recuperacao = await _service.RecuperarSenhaAsync(new RecuperarSenhaRequest
        {
            Email = req.Email
        });
        Environment.SetEnvironmentVariable("RETORNA_SENHA_TEMPORARIA", null);

        Assert.NotEmpty(recuperacao.SenhaTemporaria!);

        var loginResult = await _service.LoginAsync(new LoginRequest
        {
            Email = req.Email,
            Senha = recuperacao.SenhaTemporaria!
        });

        Assert.NotEmpty(loginResult.Token);
    }

    [Fact]
    public async Task RecuperarSenha_SenhaOriginalNaoFunciona_AposRecuperacao()
    {
        var req = Conta("invalida_pos_rec@test.com", "11222333000181");
        await _service.CriarContaAsync(req);
        await ConfirmarEmpresaEUsuario(req.Email, Normaliza(req.Cnpj));

        Environment.SetEnvironmentVariable("RETORNA_SENHA_TEMPORARIA", "true");
        await _service.RecuperarSenhaAsync(new RecuperarSenhaRequest { Email = req.Email });
        Environment.SetEnvironmentVariable("RETORNA_SENHA_TEMPORARIA", null);

        await Assert.ThrowsAsync<UnauthorizedAccessException>(() =>
            _service.LoginAsync(new LoginRequest
            {
                Email = req.Email,
                Senha = req.Senha  // senha original — deve falhar
            }));
    }

    // ─────────────────────────────────────────────────────────────────────────────
    // VALIDAR TOKEN
    // ─────────────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task ValidarToken_Valido_RetornaSessaoCorreta()
    {
        var req = Conta("token_ok@test.com", "11222333000181");
        await _service.CriarContaAsync(req);
        await ConfirmarEmpresaEUsuario(req.Email, Normaliza(req.Cnpj));

        var login = await _service.LoginAsync(new LoginRequest
        {
            Email = req.Email,
            Senha = req.Senha
        });

        var sessao = await _service.ValidarTokenAsync(login.Token);

        Assert.Equal(login.Usuario.Id, sessao.Usuario.Id);
        Assert.Equal(login.Empresa.Cnpj, sessao.Empresa.Cnpj);
    }

    [Fact]
    public async Task ValidarToken_TokenInvalido_LancaUnauthorizedAccessException()
    {
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() =>
            _service.ValidarTokenAsync("tokenfalsoqualquer"));
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData(null)]
    public async Task ValidarToken_TokenVazioOuNulo_LancaUnauthorizedAccessException(string? token)
    {
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() =>
            _service.ValidarTokenAsync(token!));
    }

    [Fact]
    public async Task ValidarToken_TokenExpirado_LancaUnauthorizedAccessException()
    {
        var req = Conta("expired@test.com", "11222333000181");
        await _service.CriarContaAsync(req);
        await ConfirmarEmpresaEUsuario(req.Email, Normaliza(req.Cnpj));

        var login = await _service.LoginAsync(new LoginRequest
        {
            Email = req.Email,
            Senha = req.Senha
        });

        await ExpirarToken(login.Token);

        await Assert.ThrowsAsync<UnauthorizedAccessException>(() =>
            _service.ValidarTokenAsync(login.Token));
    }

    // ─────────────────────────────────────────────────────────────────────────────
    // UNICIDADE DE EMAIL — PROTEÇÃO MULTITENANT
    // ─────────────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task EmailUnico_MesmoEmail_DuasEmpresas_SegundaFalha()
    {
        await _service.CriarContaAsync(Conta("global_mt@test.com", "11222333000181"));

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            _service.CriarContaAsync(Conta("global_mt@test.com", "99888777000100")));
    }

    [Fact]
    public async Task EmailUnico_EmailsDiferentes_AmbosPermitidos()
    {
        await _service.CriarContaAsync(Conta("mt_a@test.com", "11222333000181"));
        var result = await _service.CriarContaAsync(Conta("mt_b@test.com", "22333444000181"));

        Assert.NotNull(result);
        Assert.Equal("mt_b@test.com", result.Usuario.Email);
    }

    [Fact]
    public async Task IndiceEmail_GarantirColunas_Idempotente_MultiplasChamadas()
    {
        // Três instâncias diferentes do service chamam GarantirTabelasAsync sequencialmente
        var s1 = Build(_cs);
        var s2 = Build(_cs);
        var s3 = Build(_cs);

        await s1.CriarContaAsync(Conta("seq1@test.com", "11222333000181"));
        await s2.CriarContaAsync(Conta("seq2@test.com", "22333444000181"));
        await s3.CriarContaAsync(Conta("seq3@test.com", "33444555000181"));
    }

    // ─────────────────────────────────────────────────────────────────────────────
    // HELPERS
    // ─────────────────────────────────────────────────────────────────────────────

    private static CriarContaRequest Conta(
        string email,
        string cnpj = "11222333000181") => new()
        {
            Cnpj = cnpj,
            Email = email,
            UsuarioNome = "Admin Teste",
            Login = "",
            Senha = "Senha@123",
            Perfil = "Administrador",
            RazaoSocial = "Empresa Teste Ltda",
            NomeFantasia = "Empresa Teste"
        };

    private static string Normaliza(string v) => CnpjUtils.Normalizar(v);

    private async Task ConfirmarEmpresaEUsuario(string email, string cnpj)
    {
        await using var conn = new MySqlConnection(_cs);
        await conn.OpenAsync();

        await using var cmd1 = new MySqlCommand(
            "UPDATE EMPRESA SET REGISTRADO='S' WHERE CNPJ=@c", conn);
        cmd1.Parameters.AddWithValue("@c", cnpj);
        await cmd1.ExecuteNonQueryAsync();

        await using var cmd2 = new MySqlCommand(
            "UPDATE RET_USUARIO SET CONFIRMADO='S' WHERE LOWER(EMAIL)=@e", conn);
        cmd2.Parameters.AddWithValue("@e", email.ToLower());
        await cmd2.ExecuteNonQueryAsync();
    }

    private async Task DesativarUsuario(string email)
    {
        await using var conn = new MySqlConnection(_cs);
        await conn.OpenAsync();
        await using var cmd = new MySqlCommand(
            "UPDATE RET_USUARIO SET ATIVO='N' WHERE LOWER(EMAIL)=@e", conn);
        cmd.Parameters.AddWithValue("@e", email.ToLower());
        await cmd.ExecuteNonQueryAsync();
    }

    private async Task ExpirarToken(string token)
    {
        var hash = Convert.ToHexString(
            System.Security.Cryptography.SHA256.HashData(
                System.Text.Encoding.UTF8.GetBytes(token))).ToLowerInvariant();

        await using var conn = new MySqlConnection(_cs);
        await conn.OpenAsync();
        await using var cmd = new MySqlCommand(
            "UPDATE RET_SESSAO SET EXPIRA_EM = DATE_SUB(UTC_TIMESTAMP(), INTERVAL 1 HOUR) WHERE TOKEN_HASH=@h", conn);
        cmd.Parameters.AddWithValue("@h", hash);
        await cmd.ExecuteNonQueryAsync();
    }
}
