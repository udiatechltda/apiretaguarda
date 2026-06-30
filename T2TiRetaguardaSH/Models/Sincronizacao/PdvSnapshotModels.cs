using System;
using System.Collections.Generic;

namespace T2TiRetaguardaSH.Models.Sincronizacao
{
    public class PdvSnapshotRequest
    {
        public string DispositivoId { get; set; }
        public List<PdvSnapshotTable> Tabelas { get; set; } = new List<PdvSnapshotTable>();
    }

    public class PdvSnapshotTable
    {
        public string Nome { get; set; }
        public List<PdvSnapshotRecord> Registros { get; set; } = new List<PdvSnapshotRecord>();
    }

    public class PdvSnapshotRecord
    {
        public string IdLocal { get; set; }
        public string DadosJson { get; set; }
        public string Hash { get; set; }
    }

    public class PdvSnapshotResponse
    {
        public string Cnpj { get; set; }
        public string BancoOperacional { get; set; }
        public string DispositivoId { get; set; }
        public int TotalTabelas { get; set; }
        public int TotalRegistros { get; set; }
        public DateTime SincronizadoEm { get; set; }
    }
}
