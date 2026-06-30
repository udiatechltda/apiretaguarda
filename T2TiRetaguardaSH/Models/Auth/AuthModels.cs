using System;

namespace T2TiRetaguardaSH.Models.Auth
{
    public class CriarContaRequest
    {
        public string Cnpj { get; set; }
        public string RazaoSocial { get; set; }
        public string NomeFantasia { get; set; }
        public string Email { get; set; }
        public string UsuarioNome { get; set; }
        public string Login { get; set; }
        public string Senha { get; set; }
        public string Perfil { get; set; }
    }

    public class LoginRequest
    {
        public string Email { get; set; }
        public string Senha { get; set; }
    }

    public class RecuperarSenhaRequest
    {
        public string Email { get; set; }
    }

    public class RecuperarSenhaResponse
    {
        public bool Sucesso { get; set; }
        public bool SenhaAlterada { get; set; }
        public string Mensagem { get; set; }
        public string SenhaTemporaria { get; set; }
    }

    public class AuthResponse
    {
        public string Token { get; set; }
        public DateTime ExpiraEm { get; set; }
        public AuthUsuarioResponse Usuario { get; set; }
        public AuthEmpresaResponse Empresa { get; set; }
    }

    public class AuthUsuarioResponse
    {
        public int Id { get; set; }
        public string Nome { get; set; }
        public string Login { get; set; }
        public string Perfil { get; set; }
        public string Email { get; set; }
        public string Confirmado { get; set; }
    }

    public class AuthEmpresaResponse
    {
        public int Id { get; set; }
        public string Cnpj { get; set; }
        public string RazaoSocial { get; set; }
        public string NomeFantasia { get; set; }
        public string Registrado { get; set; }
        public string BancoOperacional { get; set; }
    }
}
