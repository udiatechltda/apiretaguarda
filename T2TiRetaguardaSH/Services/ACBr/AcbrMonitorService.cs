/*******************************************************************************
Title: T2Ti ERP 3.0                                                                
Description: Service relacionado ao ACBrMonitor
                                                                                
The MIT License                                                                 
                                                                                
Copyright: Copyright (C) 2021 T2Ti.COM                                          
                                                                                
Permission is hereby granted, free of charge, to any person                     
obtaining a copy of this software and associated documentation                  
files (the "Software"), to deal in the Software without                         
restriction, including without limitation the rights to use,                    
copy, modify, merge, publish, distribute, sublicense, and/or sell               
copies of the Software, and to permit persons to whom the                       
Software is furnished to do so, subject to the following                        
conditions:                                                                     
                                                                                
The above copyright notice and this permission notice shall be                  
included in all copies or substantial portions of the Software.                 
                                                                                
THE SOFTWARE IS PROVIDED "AS IS", WITHOUT WARRANTY OF ANY KIND,                 
EXPRESS OR IMPLIED, INCLUDING BUT NOT LIMITED TO THE WARRANTIES                 
OF MERCHANTABILITY, FITNESS FOR A PARTICULAR PURPOSE AND                        
NONINFRINGEMENT. IN NO EVENT SHALL THE AUTHORS OR COPYRIGHT                     
HOLDERS BE LIABLE FOR ANY CLAIM, DAMAGES OR OTHER LIABILITY,                    
WHETHER IN AN ACTION OF CONTRACT, TORT OR OTHERWISE, ARISING                    
FROM, OUT OF OR IN CONNECTION WITH THE SOFTWARE OR THE USE OR                   
OTHER DEALINGS IN THE SOFTWARE.                                                 
                                                                                
       The author may be contacted at:                                          
           t2ti.com@gmail.com                                                   
                                                                                
@author Albert Eije (alberteije@gmail.com)                    
@version 1.0.0
*******************************************************************************/
using NHibernate;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Text;
using System.Threading;
using T2TiRetaguardaSH.Models;
using T2TiRetaguardaSH.NHibernate;
using T2TiRetaguardaSH.Util;

namespace T2TiRetaguardaSH.Services
{
	public class AcbrMonitorService
    {

		private string CaminhoComCnpj = "";

		public string EmitirNfce(string numero, string cnpj, string nfceIni)
		{
			// configurações
			CaminhoComCnpj = "C:\\ACBrMonitor\\" + cnpj + "\\";
			GarantirEstruturaAcbr(CaminhoComCnpj);

			// salva o arquivo INI em disco
			string caminhoArquivoIniNfce = CaminhoComCnpj + "ini\\nfce\\" + numero + ".ini";
			byte[] arquivoIniBytes = Convert.FromBase64String(nfceIni);
			File.WriteAllBytes(caminhoArquivoIniNfce, arquivoIniBytes);

			// chama o método para criar a nota
			CriarNFe(caminhoArquivoIniNfce);
			// pega o caminho do XML criado
			string caminhoArquivoXml = PegarRetornoSaida("ARQUIVO-XML");
			// chama o método para criar e enviar a nota
			EnviarNFe(caminhoArquivoXml);
			string retorno = PegarRetornoSaida("Envio");
			if (!retorno.Contains("ERRO")) {
				// chama o método para gerar o PDF
				ImprimirDanfe(caminhoArquivoXml);
				// captura o retorno do arquivo SAI
				retorno = PegarRetornoSaida("ARQUIVO-PDF");
			}		
			return retorno;
		}

		public AcbrNfceResponse EmitirNfceJson(AcbrNfceRequest request)
		{
			if (request == null)
			{
				throw new ArgumentException("Requisicao NFC-e obrigatoria.");
			}

			string numero = request.Numero;
			string cnpj = SomenteDigitos(request.Cnpj);
			if (string.IsNullOrWhiteSpace(numero) || string.IsNullOrWhiteSpace(cnpj) || string.IsNullOrWhiteSpace(request.NfceIniBase64))
			{
				throw new ArgumentException("Numero, CNPJ e INI da NFC-e sao obrigatorios.");
			}

			if (ModoMock())
			{
				return EmitirNfceMock(numero, cnpj, request.NfceIniBase64);
			}

			string retorno = request.Contingencia
				? EmitirNfceContingencia(numero, cnpj, request.NfceIniBase64)
				: EmitirNfce(numero, cnpj, request.NfceIniBase64);

			return new AcbrNfceResponse
			{
				Sucesso = !retorno.Contains("ERRO"),
				Status = retorno.Contains("ERRO") ? "ERRO" : "AUTORIZADA",
				Mensagem = retorno.Contains("ERRO") ? retorno : "NFC-e emitida pelo ACBrMonitor.",
				CaminhoPdf = retorno.Contains("ERRO") ? "" : retorno,
				CaminhoXml = PegarUltimoXml(cnpj),
				Protocolo = PegarUltimoProtocolo(cnpj)
			};
		}

		public AcbrStatusResponse ConsultarStatusSefaz(string cnpj, string uf)
		{
			cnpj = SomenteDigitos(cnpj);
			if (ModoMock())
			{
				return new AcbrStatusResponse
				{
					Disponivel = true,
					Status = "107",
					Mensagem = "Servico em operacao no modo mock."
				};
			}

			CaminhoComCnpj = "C:\\ACBrMonitor\\" + cnpj + "\\";
			GarantirEstruturaAcbr(CaminhoComCnpj);
			ApagarArquivoSaida();
			GerarArquivoEntrada("NFE.StatusServico(" + uf + ")");
			AguardarArquivoSaida();
			string retorno = PegarRetornoSaida("Status");
			bool disponivel = !retorno.Contains("ERRO");
			return new AcbrStatusResponse
			{
				Disponivel = disponivel,
				Status = disponivel ? "107" : "ERRO",
				Mensagem = retorno
			};
		}

		public AcbrNfceResponse TransmitirContingenciaJson(AcbrNfceActionRequest request)
		{
			string cnpj = SomenteDigitos(request.Cnpj);
			if (ModoMock())
			{
				return AcaoMock("Contingencia transmitida em modo mock.");
			}

			string retorno = TransmitirNfceContingenciada(request.ChaveAcesso, cnpj);
			return RetornoAcao(retorno, cnpj);
		}

		public AcbrNfceResponse CancelarNfceJson(AcbrNfceActionRequest request)
		{
			if (ModoMock())
			{
				return AcaoMock("NFC-e cancelada em modo mock.");
			}

			var objeto = new ObjetoNfe
			{
				Cnpj = SomenteDigitos(request.Cnpj),
				ChaveAcesso = request.ChaveAcesso,
				Justificativa = request.Justificativa
			};
			string retorno = CancelarNfce(objeto);
			return RetornoAcao(retorno, objeto.Cnpj);
		}

		public AcbrNfceResponse InutilizarNumeroJson(AcbrNfceActionRequest request)
		{
			if (ModoMock())
			{
				return AcaoMock("Numero inutilizado em modo mock.");
			}

			var objeto = new ObjetoNfe
			{
				Cnpj = SomenteDigitos(request.Cnpj),
				Justificativa = request.Justificativa,
				Ano = request.Ano,
				Modelo = request.Modelo,
				Serie = request.Serie,
				NumeroInicial = request.NumeroInicial,
				NumeroFinal = request.NumeroFinal
			};
			string retorno = InutilizarNumero(objeto);
			return RetornoAcao(retorno, objeto.Cnpj);
		}

		public string EmitirNfceContingencia(string numero, string cnpj, string nfceIni)
		{
			// configurações
			CaminhoComCnpj = "C:\\ACBrMonitor\\" + cnpj + "\\";
			GarantirEstruturaAcbr(CaminhoComCnpj);

			// salva o arquivo INI em disco
			string caminhoArquivoIniNfce = CaminhoComCnpj + "ini\\nfce\\" + numero + ".ini";
			byte[] arquivoIniBytes = Convert.FromBase64String(nfceIni);
			File.WriteAllBytes(caminhoArquivoIniNfce, arquivoIniBytes);
		
			// passa para o modo de emissão off-line
			PassarParaModoOffLine();
			// chama o método para criar a nota
			CriarNFe(caminhoArquivoIniNfce);
			// pega o caminho do XML criado
			string caminhoArquivoXml = PegarRetornoSaida("ARQUIVO-XML");
			// chama o método para gerar o PDF
			ImprimirDanfe(caminhoArquivoXml);
			// captura o retorno do arquivo SAI
			string retorno = PegarRetornoSaida("ARQUIVO-PDF");
			// passa para o modo de emissão on-line
			PassarParaModoOnLine();
	  
			return retorno;
		}

		public string TransmitirNfceContingenciada(string chave, string cnpj)
		{
			// configurações
			CaminhoComCnpj = "C:\\ACBrMonitor\\" + cnpj + "\\";
			GarantirEstruturaAcbr(CaminhoComCnpj);

			string caminhoArquivoXml = CaminhoComCnpj + "LOG_NFe\\" + chave + "-nfe.xml";
			// chama o método para criar e enviar a nota
			EnviarNFe(caminhoArquivoXml);
			string retorno = PegarRetornoSaida("Envio");
			if (!retorno.Contains("ERRO")) {
				// chama o método para gerar o PDF
				ImprimirDanfe(caminhoArquivoXml);
				// captura o retorno do arquivo SAI
				retorno = PegarRetornoSaida("ARQUIVO-PDF");
			}		
			return retorno;
		}

		public string TratarNotaAnteriorContingencia(ObjetoNfe objetoNfe)
		{
			// configurações
			CaminhoComCnpj = "C:\\ACBrMonitor\\" + objetoNfe.Cnpj + "\\";
			GarantirEstruturaAcbr(CaminhoComCnpj);

			string caminhoArquivoXml = CaminhoComCnpj + "LOG_NFe\\" + objetoNfe.ChaveAcesso + "-nfe.xml";

			// vamos verificar o status da nota
			ApagarArquivoSaida();
			GerarArquivoEntrada("NFE.ConsultarNFe(" + caminhoArquivoXml + ")");
			AguardarArquivoSaida();

			string retorno = PegarRetornoSaida("Consulta");
			if (!retorno.Contains("ERRO")) {
				// se a nota anterior foi emitida = cancela. senão = inutiliza.
				if (retorno.Contains("Autorizado")) {
					retorno = CancelarNfce(objetoNfe);
				} else {
					retorno = InutilizarNumero(objetoNfe);
				}
			}
		
			return retorno;
		}

		public string InutilizarNumero(ObjetoNfe objetoNfe)
		{
			// configurações
			CaminhoComCnpj = "C:\\ACBrMonitor\\" + objetoNfe.Cnpj + "\\";
			GarantirEstruturaAcbr(CaminhoComCnpj);

			ApagarArquivoSaida();

			GerarArquivoEntrada("NFE.InutilizarNFe("
				  +objetoNfe.Cnpj +", "
				  +objetoNfe.Justificativa +", "
				  +objetoNfe.Ano +", "
				  +objetoNfe.Modelo +", "
				  +objetoNfe.Serie +", "
				  +objetoNfe.NumeroInicial +", "
				  +objetoNfe.NumeroFinal +")");

			AguardarArquivoSaida();
			return PegarRetornoSaida("Inutilizacao");
		}

		public string CancelarNfce(ObjetoNfe objetoNfe)
		{
			CaminhoComCnpj = "C:\\ACBrMonitor\\" + objetoNfe.Cnpj + "\\";
			GarantirEstruturaAcbr(CaminhoComCnpj);

			ApagarArquivoSaida();
			GerarArquivoEntrada("NFe.CANCELARNFE(" + objetoNfe.ChaveAcesso + ", " + objetoNfe.Justificativa + ", " + objetoNfe.Cnpj + ")");

			AguardarArquivoSaida();
			return PegarRetornoSaida("Cancelamento");
		}

		public string GerarPdfDanfeNfce(string chave, string cnpj)
		{
			// configurações
			CaminhoComCnpj = "C:\\ACBrMonitor\\" + cnpj + "\\";
			GarantirEstruturaAcbr(CaminhoComCnpj);

			// pega o caminho do arquivo XML da nota em contingência
			string caminhoArquivoXml = CaminhoComCnpj + "LOG_NFe\\" + chave + "-nfe.xml";
			// chama o método para gerar o PDF
			ImprimirDanfe(caminhoArquivoXml);
			// captura o retorno do arquivo SAI
			return PegarRetornoSaida("ARQUIVO-PDF");	
		}

		public void EnviarNFe(string caminhoArquivoXml)
		{
			ApagarArquivoSaida();
			GerarArquivoEntrada("NFE.EnviarNFe(" + caminhoArquivoXml + ", 001, , , , 1, , )");
			AguardarArquivoSaida();
		}

		public void CriarNFe(string caminhoArquivoIniNfce)
		{
			ApagarArquivoSaida();
			GerarArquivoEntrada("NFE.CriarNFe(" + caminhoArquivoIniNfce + ")");
			AguardarArquivoSaida();
		}

		public void ImprimirDanfe(string caminhoArquivoXml)
		{
			ApagarArquivoSaida();
			GerarArquivoEntrada("NFE.ImprimirDanfePDF(" + caminhoArquivoXml + ", , , 1,)");
			AguardarArquivoSaida();
		}

		public bool PassarParaModoOffLine()
		{
			ApagarArquivoSaida();
			GerarArquivoEntrada("NFE.SetFormaEmissao(9)"); // 9=offline
			return AguardarArquivoSaida();
		}

		public bool PassarParaModoOnLine()
		{
			ApagarArquivoSaida();
			GerarArquivoEntrada("NFE.SetFormaEmissao(1)"); // 1=normal
			return AguardarArquivoSaida();
		}

		public bool GerarZipArquivosXml(string periodo, string cnpj)
		{
			using (ISession Session = NHibernateHelper.GetSessionFactory().OpenSession())
			{
				string filtro = "CNPJ = '" + cnpj + "'";
				Empresa empresa = new EmpresaService().ConsultarObjetoFiltro(filtro);
				if (empresa != null)
				{
					string diretorio = "C:\\ACBrMonitor\\" + cnpj + "\\DFes\\" + periodo;
					string arquivo = "C:\\ACBrMonitor\\" + cnpj + "\\NotasFiscaisNFCe_" + periodo + ".zip";
					if (File.Exists(arquivo))
					{
						File.Delete(arquivo);
					}
					ZipFile.CreateFromDirectory(diretorio, arquivo);
					return true;
				}
				else
				{
					return false;
				}
			}
		}

		public void AtualizarCertificado(string certificadoBase64, string senha, string cnpj)
		{
			using (ISession Session = NHibernateHelper.GetSessionFactory().OpenSession())
			{
				string filtro = "CNPJ = '" + cnpj + "'";
				Empresa empresa = new EmpresaService().ConsultarObjetoFiltro(filtro);
				if (empresa != null)
				{
					// configura os caminhos
					string CaminhoComCnpj = "C:\\ACBrMonitor\\" + empresa.Cnpj + '\\';
					string caminhoArquivoCertificado = CaminhoComCnpj + empresa.Cnpj + ".pfx";

					// converte e salva o arquivo do certificado em disco
					byte[] certificadoBytes = Convert.FromBase64String(certificadoBase64);
					File.WriteAllBytes(caminhoArquivoCertificado, certificadoBytes);

					ApagarArquivoSaida();
					GerarArquivoEntrada("NFE.SetCertificado(" + caminhoArquivoCertificado + "," + senha + ")");
					AguardarArquivoSaida();

					Biblioteca.KillTask("ACBrMonitor_" + empresa.Cnpj + ".exe");
					string caminhoExecutavel = CaminhoComCnpj + "ACBrMonitor_" + empresa.Cnpj + ".exe";
					Process.Start(caminhoExecutavel);
				}
			}

		}

		private void GerarArquivoEntrada(string comando)
		{
			GarantirEstruturaAcbr(CaminhoComCnpj);
			StreamWriter arquivoEntrada = new StreamWriter(CaminhoComCnpj + "ent.txt", true, Encoding.ASCII);
			arquivoEntrada.Write(comando);
			arquivoEntrada.Close();		
		}

		private string PegarRetornoSaida(string operacao)
		{
			string retorno = "";
			if (!File.Exists(CaminhoComCnpj + "sai.txt"))
			{
				return "[ERRO] - ACBrMonitor nao retornou arquivo sai.txt para " + operacao + ".";
			}
			IniFile arquivoSaida = new IniFile(CaminhoComCnpj, "sai.txt");

			// carrega o conteúdo completo do arquivo
			string arquivoCompleto = System.IO.File.ReadAllText(CaminhoComCnpj + "sai.txt");

			string codigoStatus = arquivoSaida.IniReadString(operacao, "CStat", "");
			string motivo = arquivoSaida.IniReadString(operacao, "XMotivo", "");

			string caminhoArquivoXml = "";
		
			if (operacao.Equals("ARQUIVO-XML")) 
			{
				caminhoArquivoXml = arquivoCompleto;
				caminhoArquivoXml = caminhoArquivoXml.Replace("OK: ", "").Trim();
				return caminhoArquivoXml; 
			} 
			else if (operacao.Equals("ARQUIVO-PDF")) 
			{
				retorno = arquivoCompleto;
				retorno = retorno.Replace("OK: Arquivo criado em: ", "").Trim();
				return retorno;
			} 
			else if (operacao.Equals("Envio")) 
			{
				retorno = motivo;
			} 
			else if (operacao.Equals("Cancelamento")) 
			{
				retorno =  motivo;
			} 
			else if (operacao.Equals("Consulta")) 
			{
				retorno = motivo;
			} 
			else if (operacao.Equals("Inutilizacao")) 
			{
				return arquivoCompleto;
			}
			
			List<string> listaStatus = new List<string>{ "", "100", "102", "135" }; // se o status não for um dos que estiverem nessa lista, vamos retornar um erro.
			
			if (!listaStatus.Contains(codigoStatus)) {
				return "[ERRO] - [" + codigoStatus + "] " + motivo;
			}

			return retorno;
		}

		private bool ApagarArquivoSaida()
		{
			string arquivo = CaminhoComCnpj + "sai.txt";
			if (File.Exists(arquivo))
			{
				File.Delete(arquivo);
			}
			return true;
		}

		private bool AguardarArquivoSaida()
		{
			int tempoEspera = 0;
			while (!File.Exists(CaminhoComCnpj + "sai.txt"))
			{
				Thread.Sleep(1000);
				tempoEspera++;

				if (tempoEspera > 30)
				{
					return false;
				}
			}
			return true;
		}

		private static bool ModoMock()
		{
			string value = Environment.GetEnvironmentVariable("ACBR_MONITOR_MOCK");
			return string.Equals(value, "true", StringComparison.OrdinalIgnoreCase);
		}

		private static void GarantirEstruturaAcbr(string caminhoComCnpj)
		{
			if (string.IsNullOrWhiteSpace(caminhoComCnpj))
			{
				return;
			}

			Directory.CreateDirectory(caminhoComCnpj);
			Directory.CreateDirectory(Path.Combine(caminhoComCnpj, "ini", "nfce"));
			Directory.CreateDirectory(Path.Combine(caminhoComCnpj, "LOG_NFe"));
			Directory.CreateDirectory(Path.Combine(caminhoComCnpj, "DFes"));
		}

		private static string SomenteDigitos(string value)
		{
			if (string.IsNullOrWhiteSpace(value))
			{
				return "";
			}

			StringBuilder sb = new StringBuilder();
			foreach (char c in value)
			{
				if (char.IsDigit(c))
				{
					sb.Append(c);
				}
			}
			return sb.ToString();
		}

		private AcbrNfceResponse EmitirNfceMock(string numero, string cnpj, string nfceIniBase64)
		{
			string raiz = Environment.GetEnvironmentVariable("ACBR_MONITOR_MOCK_ROOT");
			if (string.IsNullOrWhiteSpace(raiz))
			{
				raiz = Path.Combine(Path.GetTempPath(), "ACBrMonitorMock");
			}

			string caminho = Path.Combine(raiz, cnpj);
			Directory.CreateDirectory(Path.Combine(caminho, "ini", "nfce"));
			Directory.CreateDirectory(Path.Combine(caminho, "LOG_NFe"));
			Directory.CreateDirectory(Path.Combine(caminho, "PDF"));

			string iniPath = Path.Combine(caminho, "ini", "nfce", numero + ".ini");
			File.WriteAllBytes(iniPath, Convert.FromBase64String(nfceIniBase64));

			string xmlPath = Path.Combine(caminho, "LOG_NFe", numero + "-nfe.xml");
			string pdfPath = Path.Combine(caminho, "PDF", numero + ".pdf");
			File.WriteAllText(xmlPath, "<nfeMock numero=\"" + numero + "\" cnpj=\"" + cnpj + "\" />", Encoding.UTF8);
			File.WriteAllText(pdfPath, "PDF MOCK NFC-e " + numero, Encoding.UTF8);

			return new AcbrNfceResponse
			{
				Sucesso = true,
				Status = "AUTORIZADA",
				Mensagem = "NFC-e emitida em modo mock local.",
				CaminhoPdf = pdfPath,
				CaminhoXml = xmlPath,
				Protocolo = "MOCK-" + numero
			};
		}

		private static AcbrNfceResponse AcaoMock(string mensagem)
		{
			return new AcbrNfceResponse
			{
				Sucesso = true,
				Status = "AUTORIZADA",
				Mensagem = mensagem,
				Protocolo = "MOCK"
			};
		}

		private AcbrNfceResponse RetornoAcao(string retorno, string cnpj)
		{
			return new AcbrNfceResponse
			{
				Sucesso = !retorno.Contains("ERRO"),
				Status = retorno.Contains("ERRO") ? "ERRO" : "AUTORIZADA",
				Mensagem = retorno,
				CaminhoPdf = retorno.Contains("ERRO") ? "" : retorno,
				CaminhoXml = PegarUltimoXml(cnpj),
				Protocolo = PegarUltimoProtocolo(cnpj)
			};
		}

		private static string PegarUltimoXml(string cnpj)
		{
			string diretorio = "C:\\ACBrMonitor\\" + cnpj + "\\LOG_NFe\\";
			if (!Directory.Exists(diretorio))
			{
				return "";
			}

			FileInfo arquivo = new DirectoryInfo(diretorio)
				.GetFiles("*-nfe.xml")
				.OrderByDescending(f => f.LastWriteTimeUtc)
				.FirstOrDefault();

			return arquivo == null ? "" : arquivo.FullName;
		}

		private static string PegarUltimoProtocolo(string cnpj)
		{
			string diretorio = "C:\\ACBrMonitor\\" + cnpj + "\\LOG_NFe\\";
			if (!Directory.Exists(diretorio))
			{
				return "";
			}

			FileInfo arquivo = new DirectoryInfo(diretorio)
				.GetFiles("*-nfe.xml")
				.OrderByDescending(f => f.LastWriteTimeUtc)
				.FirstOrDefault();

			return arquivo == null ? "" : arquivo.LastWriteTimeUtc.ToString("yyyyMMddHHmmss");
		}

    }

}
