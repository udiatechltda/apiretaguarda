namespace T2TiRetaguardaSH.Models
{
    public class AcbrNfceRequest
    {
        public string Numero { get; set; }
        public string Cnpj { get; set; }
        public string NfceIniBase64 { get; set; }
        public bool Contingencia { get; set; }
    }
}
