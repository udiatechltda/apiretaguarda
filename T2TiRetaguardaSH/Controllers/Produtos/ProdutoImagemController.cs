using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using System;
using System.Threading.Tasks;
using T2TiRetaguardaSH.Models;
using T2TiRetaguardaSH.Services.Auth;
using T2TiRetaguardaSH.Services.Produtos;

namespace T2TiRetaguardaSH.Controllers.Produtos
{
    [Route("api")]
    [Produces("application/json")]
    public class ProdutoImagemController : Controller
    {
        private readonly AuthService _authService;
        private readonly ProdutoImagemService _produtoImagemService;

        public ProdutoImagemController(AuthService authService, ProdutoImagemService produtoImagemService)
        {
            _authService = authService;
            _produtoImagemService = produtoImagemService;
        }

        [HttpPost("imagens")]
        [RequestSizeLimit(10 * 1024 * 1024)]
        public async Task<IActionResult> Upload([FromForm] int produtoId, [FromForm] IFormFile arquivo)
        {
            try
            {
                var sessao = await _authService.ValidarTokenAsync(ExtrairToken());
                var resposta = await _produtoImagemService.SalvarImagemAsync(sessao.Empresa.Id, sessao.Empresa.Cnpj, produtoId, arquivo, Request);
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
                return StatusCode(500, new RetornoJsonErro(500, "Erro no servidor [Upload Imagem Produto]", ex));
            }
        }

        [HttpGet("produtos/imagens/manifest")]
        public async Task<IActionResult> Manifest()
        {
            try
            {
                var sessao = await _authService.ValidarTokenAsync(ExtrairToken());
                var resposta = await _produtoImagemService.ObterManifestAsync(sessao.Empresa.Id, sessao.Empresa.Cnpj, Request);
                return Ok(resposta);
            }
            catch (UnauthorizedAccessException ex)
            {
                return StatusCode(401, new RetornoJsonErro(401, ex.Message, null));
            }
            catch (Exception ex)
            {
                return StatusCode(500, new RetornoJsonErro(500, "Erro no servidor [Manifest Imagens Produto]", ex));
            }
        }

        [HttpGet("produtos/{produtoId}/imagem")]
        public async Task<IActionResult> Download(int produtoId)
        {
            try
            {
                var sessao = await _authService.ValidarTokenAsync(ExtrairToken());
                var arquivo = await _produtoImagemService.ObterArquivoAsync(sessao.Empresa.Id, produtoId);
                if (arquivo == null)
                    return NotFound(new RetornoJsonErro(404, "Imagem do produto nao encontrada.", null));

                return PhysicalFile(arquivo.Caminho, arquivo.ContentType ?? "application/octet-stream", arquivo.NomeArquivo);
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
                return StatusCode(500, new RetornoJsonErro(500, "Erro no servidor [Download Imagem Produto]", ex));
            }
        }

        [HttpDelete("produtos/{produtoId}/imagem")]
        public async Task<IActionResult> Delete(int produtoId)
        {
            try
            {
                var sessao = await _authService.ValidarTokenAsync(ExtrairToken());
                await _produtoImagemService.RemoverImagemAsync(sessao.Empresa.Id, produtoId);
                return Ok(new { produtoId, removido = true });
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
                return StatusCode(500, new RetornoJsonErro(500, "Erro no servidor [Remover Imagem Produto]", ex));
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
