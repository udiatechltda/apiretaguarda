using System;
using System.Collections.Generic;

namespace T2TiRetaguardaSH.Models.Produtos
{
    public class ProdutoImagemUploadResponse
    {
        public int ProdutoId { get; set; }
        public string Url { get; set; }
        public string CaminhoRelativo { get; set; }
        public string Hash { get; set; }
        public long TamanhoBytes { get; set; }
        public string ContentType { get; set; }
        public DateTime AtualizadoEm { get; set; }
    }

    public class ProdutoImagemManifestItem
    {
        public int ProdutoId { get; set; }
        public string Url { get; set; }
        public string CaminhoRelativo { get; set; }
        public string Hash { get; set; }
        public long TamanhoBytes { get; set; }
        public string ContentType { get; set; }
        public DateTime AtualizadoEm { get; set; }
        public bool Excluido { get; set; }
    }

    public class ProdutoImagemManifestResponse
    {
        public string Cnpj { get; set; }
        public List<ProdutoImagemManifestItem> Imagens { get; set; } = new List<ProdutoImagemManifestItem>();
    }
}
