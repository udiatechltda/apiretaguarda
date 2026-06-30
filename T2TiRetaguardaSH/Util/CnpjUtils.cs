using System;
using System.Text.RegularExpressions;

namespace T2TiRetaguardaSH.Util
{
    /// <summary>
    /// Valida e formata CNPJ numérico e alfanumérico (novo formato 2026).
    /// Algoritmo: os pesos são {6,5,4,3,2,9,8,7,6,5,4,3,2} e o valor de cada
    /// caractere é calculado como (int)char - (int)'0', ou seja, letras valem
    /// A=17, B=18, ... Z=42, o que permite DV correto para bases com letras.
    /// Os dois dígitos verificadores (DV) são sempre numéricos.
    /// </summary>
    public static class CnpjUtils
    {
        private static readonly int[] Pesos = { 6, 5, 4, 3, 2, 9, 8, 7, 6, 5, 4, 3, 2 };

        /// <summary>Remove '.', '/' e '-' e converte para maiúsculas. Não extrai só dígitos.</summary>
        public static string Normalizar(string cnpj)
        {
            if (string.IsNullOrWhiteSpace(cnpj)) return string.Empty;
            return Regex.Replace(cnpj.Trim().ToUpperInvariant(), @"[.\-/]", "");
        }

        /// <summary>Formata como XX.XXX.XXX/XXXX-XX. Retorna o valor sem formatação se inválido.</summary>
        public static string Formatar(string cnpj)
        {
            var n = Normalizar(cnpj);
            if (n.Length != 14) return n;
            return $"{n[..2]}.{n[2..5]}.{n[5..8]}/{n[8..12]}-{n[12..]}";
        }

        /// <summary>
        /// Valida o CNPJ: primeiros 12 chars devem ser [A-Z0-9], últimos 2 devem ser dígitos,
        /// não pode ser tudo zeros e os DVs devem estar corretos.
        /// </summary>
        public static bool IsValido(string cnpj)
        {
            var n = Normalizar(cnpj);
            if (n.Length != 14) return false;
            if (!Regex.IsMatch(n[..12], @"^[A-Z0-9]+$")) return false;
            if (!Regex.IsMatch(n[12..], @"^\d{2}$")) return false;
            if (n.Replace("0", "").Length == 0) return false;

            return CalcularDV(n[..12]) == n[12..];
        }

        /// <summary>Calcula os dois dígitos verificadores para uma base de 12 caracteres [A-Z0-9].</summary>
        public static string CalcularDV(string base12)
        {
            var n = Normalizar(base12);
            if (n.Length != 12 || !Regex.IsMatch(n, @"^[A-Z0-9]+$"))
                throw new ArgumentException($"Base CNPJ invalida para calculo de DV: {base12}");

            var d1 = CalcularDigito(n).ToString();
            var d2 = CalcularDigito(n + d1).ToString();
            return d1 + d2;
        }

        private static int CalcularDigito(string s)
        {
            int soma = 0;
            for (int i = s.Length - 1; i >= 0; i--)
                soma += ((int)s[i] - '0') * Pesos[Pesos.Length - s.Length + i];
            int resto = soma % 11;
            return resto < 2 ? 0 : 11 - resto;
        }
    }
}
