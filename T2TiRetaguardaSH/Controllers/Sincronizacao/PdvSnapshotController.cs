using Microsoft.AspNetCore.Mvc;
using System;
using System.Threading.Tasks;
using T2TiRetaguardaSH.Models;
using T2TiRetaguardaSH.Models.Sincronizacao;
using T2TiRetaguardaSH.Services.Auth;
using T2TiRetaguardaSH.Services.Sincronizacao;

namespace T2TiRetaguardaSH.Controllers.Sincronizacao
{
    [Route("sincroniza/pdv")]
    [Produces("application/json")]
    public class PdvSnapshotController : Controller
    {
        private readonly AuthService _authService;
        private readonly PdvSnapshotService _snapshotService;

        public PdvSnapshotController(AuthService authService, PdvSnapshotService snapshotService)
        {
            _authService = authService;
            _snapshotService = snapshotService;
        }

        [HttpPost("snapshot")]
        public async Task<IActionResult> Snapshot([FromBody] PdvSnapshotRequest request)
        {
            try
            {
                var sessao = await _authService.ValidarTokenAsync(ExtrairToken());
                var resposta = await _snapshotService.SalvarSnapshotAsync(sessao.Empresa.Id, sessao.Empresa.Cnpj, request);
                return Ok(resposta);
            }
            catch (UnauthorizedAccessException ex)
            {
                return StatusCode(401, new RetornoJsonErro(401, ex.Message, null));
            }
            catch (ArgumentException ex)
            {
                return StatusCode(400, new RetornoJsonErro(400, ex.Message, null));
            }
            catch (Exception ex)
            {
                return StatusCode(500, new RetornoJsonErro(500, "Erro no servidor [Snapshot PDV]", ex));
            }
        }

        private string ExtrairToken()
        {
            var authorization = Request.Headers["Authorization"].ToString();
            return authorization.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase)
                ? authorization.Substring("Bearer ".Length).Trim()
                : authorization;
        }
    }
}
