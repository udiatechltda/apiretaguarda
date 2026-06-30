using T2TiRetaguardaSH.Util;
using Xunit;

namespace T2TiRetaguardaSH.Tests.Util;

public class CnpjUtilsTests
{
    // ─────────────────────────────────────────────────────────────────────────────
    // NORMALIZAR
    // ─────────────────────────────────────────────────────────────────────────────

    [Theory]
    [InlineData("11.222.333/0001-81", "11222333000181")]
    [InlineData("11222333000181",     "11222333000181")]   // já sem máscara
    [InlineData("ab.cd1.234/efgh-46", "ABCD1234EFGH46")]  // minúsculas + máscara
    [InlineData("ABCD1234EFGH46",     "ABCD1234EFGH46")]  // maiúsculas sem máscara
    [InlineData("PM.0O3.6A7/0001-71", "PM0O36A7000171")]  // CNPJ do ticket PDV-40
    [InlineData("  11222333000181  ", "11222333000181")]   // espaços externos
    public void Normalizar_RemoveFormatacaoEUppercase(string entrada, string esperado)
    {
        Assert.Equal(esperado, CnpjUtils.Normalizar(entrada));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void Normalizar_EntradaVazia_RetornaStringVazia(string? entrada)
    {
        Assert.Equal(string.Empty, CnpjUtils.Normalizar(entrada!));
    }

    // ─────────────────────────────────────────────────────────────────────────────
    // ISVALIDO — válidos
    // ─────────────────────────────────────────────────────────────────────────────

    [Theory]
    [InlineData("11222333000181")]               // numérico sem máscara
    [InlineData("11.222.333/0001-81")]           // numérico com máscara
    [InlineData("99888777000100")]               // outro numérico válido
    [InlineData("ABCD1234EFGH46")]              // alfanumérico sem máscara
    [InlineData("abcd1234efgh46")]              // alfanumérico minúsculas
    [InlineData("AB.CD1.234/EFGH-46")]          // alfanumérico com máscara
    [InlineData("PM.0O3.6A7/0001-71")]          // CNPJ do ticket PDV-40
    [InlineData("PM0O36A7000171")]              // mesmo sem máscara
    public void IsValido_CnpjValido_RetornaTrue(string cnpj)
    {
        Assert.True(CnpjUtils.IsValido(cnpj));
    }

    // ─────────────────────────────────────────────────────────────────────────────
    // ISVALIDO — inválidos
    // ─────────────────────────────────────────────────────────────────────────────

    [Theory]
    [InlineData("123")]                  // muito curto
    [InlineData("1234567890123")]        // 13 dígitos (sem DV)
    [InlineData("")]                     // vazio
    [InlineData("   ")]                  // só espaços
    [InlineData(null)]                   // nulo
    [InlineData("ABCD1234EFGH00")]      // DV incorreto
    [InlineData("ABCD1234EFGHAA")]      // DV não-numérico
    [InlineData("00000000000000")]      // tudo zeros
    [InlineData("11111111111111")]      // todos iguais (DV incorreto)
    [InlineData("11222333000182")]      // DV trocado (correto seria 81)
    [InlineData("PM.0O3.6A7/0001-72")] // DV errado no ticket
    public void IsValido_CnpjInvalido_RetornaFalse(string? cnpj)
    {
        Assert.False(CnpjUtils.IsValido(cnpj!));
    }

    // ─────────────────────────────────────────────────────────────────────────────
    // CALCULAR DV
    // ─────────────────────────────────────────────────────────────────────────────

    [Theory]
    [InlineData("112223330001", "81")]   // numérico — DV da empresa de teste
    [InlineData("998887770001", "00")]   // numérico — DV = 00
    [InlineData("ABCD1234EFGH", "46")]  // alfanumérico — caso dos testes Java de referência
    [InlineData("PM0O36A70001", "71")]  // alfanumérico — CNPJ do ticket PDV-40
    [InlineData("abcd1234efgh", "46")]  // minúsculas devem ser normalizadas
    public void CalcularDV_BaseValida_RetornaDVCorreto(string base12, string dvEsperado)
    {
        Assert.Equal(dvEsperado, CnpjUtils.CalcularDV(base12));
    }

    [Theory]
    [InlineData("123")]          // muito curto
    [InlineData("1234567890123456")]  // muito longo
    [InlineData("ABCD1234EFG!")]     // caractere inválido
    public void CalcularDV_BaseInvalida_LancaArgumentException(string base12)
    {
        Assert.Throws<ArgumentException>(() => CnpjUtils.CalcularDV(base12));
    }

    // ─────────────────────────────────────────────────────────────────────────────
    // FORMATAR
    // ─────────────────────────────────────────────────────────────────────────────

    [Theory]
    [InlineData("11222333000181",  "11.222.333/0001-81")]
    [InlineData("ABCD1234EFGH46", "AB.CD1.234/EFGH-46")]
    [InlineData("PM0O36A7000171", "PM.0O3.6A7/0001-71")]
    [InlineData("99888777000100", "99.888.777/0001-00")]
    public void Formatar_Cnpj14Chars_AplicaMascara(string entrada, string esperado)
    {
        Assert.Equal(esperado, CnpjUtils.Formatar(entrada));
    }

    [Fact]
    public void Formatar_CnpjJaMascarado_RetornaFormatadoCorreto()
    {
        // Normaliza primeiro e aplica máscara
        Assert.Equal("11.222.333/0001-81", CnpjUtils.Formatar("11.222.333/0001-81"));
    }

    [Theory]
    [InlineData("123")]
    [InlineData("")]
    public void Formatar_TamanhoInvalido_RetornaSemMascara(string entrada)
    {
        // Se não tem 14 chars depois de normalizar, devolve como veio
        Assert.Equal(CnpjUtils.Normalizar(entrada), CnpjUtils.Formatar(entrada));
    }

    // ─────────────────────────────────────────────────────────────────────────────
    // CONSISTÊNCIA: IsValido + Formatar + Normalizar devem ser coerentes
    // ─────────────────────────────────────────────────────────────────────────────

    [Theory]
    [InlineData("11222333000181")]
    [InlineData("ABCD1234EFGH46")]
    [InlineData("PM0O36A7000171")]
    public void Formatado_QuandoRevalidado_AindaEhValido(string cnpj)
    {
        var formatado = CnpjUtils.Formatar(cnpj);
        Assert.True(CnpjUtils.IsValido(formatado),
            $"CNPJ {cnpj} formatado como '{formatado}' deve continuar válido");
    }

    [Theory]
    [InlineData("11.222.333/0001-81")]
    [InlineData("AB.CD1.234/EFGH-46")]
    [InlineData("PM.0O3.6A7/0001-71")]
    public void Normalizado_QuandoRevalidado_AindaEhValido(string cnpjFormatado)
    {
        var normalizado = CnpjUtils.Normalizar(cnpjFormatado);
        Assert.True(CnpjUtils.IsValido(normalizado),
            $"CNPJ '{cnpjFormatado}' normalizado como '{normalizado}' deve ser válido");
    }
}
