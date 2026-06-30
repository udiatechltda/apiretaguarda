using Microsoft.AspNetCore.Mvc;
using System;
using System.Threading.Tasks;
using T2TiRetaguardaSH.Models;
using T2TiRetaguardaSH.Models.Auth;
using T2TiRetaguardaSH.Services.Auth;

namespace T2TiRetaguardaSH.Controllers.Auth
{
    [Route("auth")]
    [Produces("application/json")]
    public class AuthController : Controller
    {
        private readonly AuthService _service;

        public AuthController(AuthService service)
        {
            _service = service;
        }

        [HttpPost("criar-conta")]
        public async Task<IActionResult> CriarConta([FromBody] CriarContaRequest request)
        {
            try
            {
                return Ok(await _service.CriarContaAsync(request));
            }
            catch (ArgumentException ex)
            {
                return StatusCode(400, new RetornoJsonErro(400, ex.Message, null));
            }
            catch (InvalidOperationException ex)
            {
                return StatusCode(400, new RetornoJsonErro(400, ex.Message, null));
            }
            catch (Exception ex)
            {
                return StatusCode(500, new RetornoJsonErro(500, "Erro no servidor [Criar Conta]", ex));
            }
        }

        [HttpPost("login")]
        public async Task<IActionResult> Login([FromBody] LoginRequest request)
        {
            try
            {
                return Ok(await _service.LoginAsync(request));
            }
            catch (ArgumentException ex)
            {
                return StatusCode(400, new RetornoJsonErro(400, ex.Message, null));
            }
            catch (UnauthorizedAccessException ex)
            {
                return StatusCode(401, new RetornoJsonErro(401, ex.Message, null));
            }
            catch (Exception ex)
            {
                return StatusCode(500, new RetornoJsonErro(500, "Erro no servidor [Login]", ex));
            }
        }

        [HttpPost("recuperar-senha")]
        public async Task<IActionResult> RecuperarSenha([FromBody] RecuperarSenhaRequest request)
        {
            try
            {
                return Ok(await _service.RecuperarSenhaAsync(request));
            }
            catch (ArgumentException ex)
            {
                return StatusCode(400, new RetornoJsonErro(400, ex.Message, null));
            }
            catch (Exception ex)
            {
                return StatusCode(500, new RetornoJsonErro(500, "Erro no servidor [Recuperar Senha]", ex));
            }
        }

        [HttpGet("me")]
        public async Task<IActionResult> Me()
        {
            try
            {
                var authorization = Request.Headers["Authorization"].ToString();
                var token = authorization.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase)
                    ? authorization.Substring("Bearer ".Length).Trim()
                    : authorization;

                return Ok(await _service.ValidarTokenAsync(token));
            }
            catch (UnauthorizedAccessException ex)
            {
                return StatusCode(401, new RetornoJsonErro(401, ex.Message, null));
            }
            catch (Exception ex)
            {
                return StatusCode(500, new RetornoJsonErro(500, "Erro no servidor [Validar Token]", ex));
            }
        }
    }
}
