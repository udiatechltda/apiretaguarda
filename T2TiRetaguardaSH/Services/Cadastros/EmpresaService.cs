/*******************************************************************************
Title: T2Ti ERP 3.0                                                                
Description: Service relacionado à tabela [EMPRESA] 
                                                                                
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
using MySql.Data.MySqlClient;
using NHibernate;
using System;
using System.Collections.Generic;
using System.IO;
using T2TiRetaguardaSH.Models;
using T2TiRetaguardaSH.NHibernate;
using T2TiRetaguardaSH.Util;

namespace T2TiRetaguardaSH.Services
{
    public class EmpresaService
    {
        private static string ObterConnectionString()
        {
            return Biblioteca.Config?["ConnectionStrings:DefaultConnection"]
                ?? "Server=localhost;Port=3306;Database=retaguarda_sh;Uid=t2ti_user;Pwd=123456;";
        }

        public Empresa ConsultarObjetoPorCnpjDireto(string cnpj)
        {
            using var connection = new MySqlConnection(ObterConnectionString());
            connection.Open();

            using var command = new MySqlCommand(@"
                SELECT ID, RAZAO_SOCIAL, NOME_FANTASIA, CNPJ, EMAIL, REGISTRADO, DATA_REGISTRO, HORA_REGISTRO
                  FROM EMPRESA
                 WHERE CNPJ = @cnpj
                 LIMIT 1", connection);
            command.Parameters.AddWithValue("@cnpj", cnpj);

            using var reader = command.ExecuteReader();
            if (!reader.Read())
                return null;

            return new Empresa
            {
                Id = Convert.ToInt32(reader["ID"]),
                RazaoSocial = reader["RAZAO_SOCIAL"] == DBNull.Value ? null : reader["RAZAO_SOCIAL"].ToString(),
                NomeFantasia = reader["NOME_FANTASIA"] == DBNull.Value ? null : reader["NOME_FANTASIA"].ToString(),
                Cnpj = reader["CNPJ"] == DBNull.Value ? null : reader["CNPJ"].ToString(),
                Email = reader["EMAIL"] == DBNull.Value ? null : reader["EMAIL"].ToString(),
                Registrado = reader["REGISTRADO"] == DBNull.Value ? null : reader["REGISTRADO"].ToString(),
                DataRegistro = reader["DATA_REGISTRO"] == DBNull.Value ? null : Convert.ToDateTime(reader["DATA_REGISTRO"]),
                HoraRegistro = reader["HORA_REGISTRO"] == DBNull.Value ? null : reader["HORA_REGISTRO"].ToString()
            };
        }

        private static void GarantirColunasUsuario(MySqlConnection connection)
        {
            GarantirColunaUsuario(connection, "EMAIL", "ALTER TABLE RET_USUARIO ADD COLUMN EMAIL VARCHAR(180) NULL AFTER LOGIN");
            GarantirColunaUsuario(connection, "CONFIRMADO", "ALTER TABLE RET_USUARIO ADD COLUMN CONFIRMADO CHAR(1) NOT NULL DEFAULT 'S' AFTER PERFIL");
            GarantirColunaUsuario(connection, "CONFIRMADO_EM", "ALTER TABLE RET_USUARIO ADD COLUMN CONFIRMADO_EM DATETIME NULL AFTER CONFIRMADO");
        }

        private static void GarantirColunaUsuario(MySqlConnection connection, string coluna, string alterSql)
        {
            using var command = new MySqlCommand(@"
                SELECT COUNT(*)
                  FROM INFORMATION_SCHEMA.COLUMNS
                 WHERE TABLE_SCHEMA = DATABASE()
                   AND TABLE_NAME = 'RET_USUARIO'
                   AND COLUMN_NAME = @coluna", connection);
            command.Parameters.AddWithValue("@coluna", coluna);

            if (Convert.ToInt32(command.ExecuteScalar()) == 0)
            {
                using var alter = new MySqlCommand(alterSql, connection);
                alter.ExecuteNonQuery();
            }
        }

        private static string NormalizarLogin(string login)
        {
            return (login ?? string.Empty).Trim().ToLowerInvariant();
        }

        private static string CodigoUsuario(string cnpj, string login)
        {
            return Biblioteca.MD5String(cnpj + ":" + NormalizarLogin(login) + Constantes.CHAVE);
        }

        public IEnumerable<Empresa> ConsultarLista()
        {
            IList<Empresa> Resultado = null;
            using (ISession Session = NHibernateHelper.GetSessionFactory().OpenSession())
            {
                NHibernateDAL<Empresa> DAL = new NHibernateDAL<Empresa>(Session);
                Resultado = DAL.Select(new Empresa());
            }
            return Resultado;
        }

        public IEnumerable<Empresa> ConsultarListaFiltro(Filtro filtro)
        {
            IList<Empresa> Resultado = null;
            using (ISession Session = NHibernateHelper.GetSessionFactory().OpenSession())
            {
                var consultaSql = "from Empresa where " + filtro.Where;
                NHibernateDAL<Empresa> DAL = new NHibernateDAL<Empresa>(Session);
                Resultado = DAL.SelectListaSql<Empresa>(consultaSql);
            }
            return Resultado;
        }
        
        public Empresa ConsultarObjeto(int id)
        {
            Empresa Resultado = null;
            using (ISession Session = NHibernateHelper.GetSessionFactory().OpenSession())
            {
                NHibernateDAL<Empresa> DAL = new NHibernateDAL<Empresa>(Session);
                Resultado = DAL.SelectId<Empresa>(id);
            }
            return Resultado;
        }

        public Empresa ConsultarObjetoFiltro(string filtro)
        {
            Empresa Resultado = null;
            using (ISession Session = NHibernateHelper.GetSessionFactory().OpenSession())
            {
                var consultaSql = "from Empresa where " + filtro;
                NHibernateDAL<Empresa> DAL = new NHibernateDAL<Empresa>(Session);
                Resultado = DAL.SelectObjetoSql<Empresa>(consultaSql);
            }
            return Resultado;
        }

        public EmpresaModel ConsultarObjetoModelFiltro(string filtro)
        {
            EmpresaModel Resultado = null;
            using (ISession Session = NHibernateHelper.GetSessionFactory().OpenSession())
            {
                var consultaSql = "from EmpresaModel where " + filtro;
                NHibernateDAL<Empresa> DAL = new NHibernateDAL<Empresa>(Session);
                Resultado = DAL.SelectObjetoSql<EmpresaModel>(consultaSql);
            }
            return Resultado;
        }

        public void Inserir(Empresa objeto)
        {
            using (ISession Session = NHibernateHelper.GetSessionFactory().OpenSession())
            {
                NHibernateDAL<Empresa> DAL = new NHibernateDAL<Empresa>(Session);
                DAL.SaveOrUpdate(objeto);
                Session.Flush();
            }
        }

        public void Alterar(Empresa objeto)
        {
            using (ISession Session = NHibernateHelper.GetSessionFactory().OpenSession())
            {
                NHibernateDAL<Empresa> DAL = new NHibernateDAL<Empresa>(Session);
                DAL.SaveOrUpdate(objeto);
                Session.Flush();
            }
        }

        public void Atualizar(Empresa objeto)
        {
            using (ISession Session = NHibernateHelper.GetSessionFactory().OpenSession())
            {
                NHibernateDAL<Empresa> DAL = new NHibernateDAL<Empresa>(Session);
                // TODO: salva a imagem em disco
                objeto.Logotipo = "";
                DAL.SaveOrUpdate(objeto);
                Session.Flush();
            }
        }

        public void Registrar(Empresa objeto)
        {
            using (ISession Session = NHibernateHelper.GetSessionFactory().OpenSession())
            {
                NHibernateDAL<Empresa> DAL = new NHibernateDAL<Empresa>(Session);
                objeto.Logotipo = "";

                string filtro = "Cnpj = '" + objeto.Cnpj + "'";
                Empresa empresa = new EmpresaService().ConsultarObjetoFiltro(filtro);
                if (empresa != null)
                {
                    if (empresa.Registrado != "P")
                    {
                        objeto.Id = empresa.Id;
                        objeto.Registrado = "P";
                        DAL.SaveOrUpdate(objeto);
                        Session.Flush();
                        EnviarEmailConfirmacao(objeto);
                    }
                }                                
            }
        }

        public EmpresaModel RegistrarEmpresaErp(EmpresaModel objeto)
        {
            using (ISession Session = NHibernateHelper.GetSessionFactory().OpenSession())
            {
                NHibernateDAL<EmpresaModel> DAL = new NHibernateDAL<EmpresaModel>(Session);
                objeto.Logotipo = "";

                string filtro = "Cnpj = '" + objeto.Cnpj + "'";
                EmpresaModel empresa = new EmpresaService().ConsultarObjetoModelFiltro(filtro);
                if (empresa != null)
                {
                    objeto.Id = empresa.Id;
                    objeto.Registrado = "S";
                    DAL.SaveOrUpdate(objeto);
                    Session.Flush();
                    //EnviarEmailConfirmacao(objeto);
                    GerarBancoDeDados(objeto.Cnpj);
                    return empresa;
                } else
                {
                    return empresa;// TODO: verifique o plano de pagamento, da forma que está aqui basta a empresa está cadastrada para ter acesso ao sistema
                }
            }
        }

        public static void GerarBancoDeDados(string cnpj)
        {
            // CORRIGIDO: Aponta para o container do Docker com a senha correta
            string connectionString = "Server=t2ti-db-mysql;Port=3306;Uid=root;Pwd=123456;";
            
            using (var connection = new MySqlConnection(connectionString))
            {
                connection.Open();
                using (var command = connection.CreateCommand())
                {
                    // Criar o banco de dados
                    command.CommandText = $"CREATE DATABASE IF NOT EXISTS `{cnpj}` DEFAULT CHARACTER SET utf8mb4 COLLATE utf8mb4_0900_ai_ci;";
                    command.ExecuteNonQuery();

                    // Selecionar o banco de dados
                    command.CommandText = $"USE `{cnpj}`;";
                    command.ExecuteNonQuery();

                    // Ler o conteúdo do arquivo de script SQL
                    string script = File.ReadAllText("dump-t2ti-erp3.sql");

                    // Executar cada consulta SQL do script
                    foreach (var query in script.Split(new[] { ';' }, StringSplitOptions.RemoveEmptyEntries))
                    {
                        command.CommandText = query.Trim();
                        command.ExecuteNonQuery();
                    }
                }
            }
        }

        public void EnviarEmailConfirmacao(Empresa objeto)
        {
            EnviarEmailConfirmacao(objeto, null);
        }

        public void EnviarEmailConfirmacao(Empresa objeto, string login)
        {
            string destino = objeto.Email;
            string nome = objeto.NomeFantasia;
            string codigo = Biblioteca.MD5String(objeto.Cnpj + Constantes.CHAVE);

            if (!string.IsNullOrWhiteSpace(login))
            {
                using var connection = new MySqlConnection(ObterConnectionString());
                connection.Open();
                GarantirColunasUsuario(connection);

                using var command = new MySqlCommand(@"
                    SELECT u.NOME, COALESCE(u.EMAIL, e.EMAIL) AS EMAIL
                      FROM RET_USUARIO u
                      JOIN EMPRESA e ON e.ID = u.ID_EMPRESA
                     WHERE e.CNPJ = @cnpj
                       AND LOWER(u.LOGIN) = @login
                     LIMIT 1", connection);
                command.Parameters.AddWithValue("@cnpj", objeto.Cnpj);
                command.Parameters.AddWithValue("@login", NormalizarLogin(login));

                using var reader = command.ExecuteReader();
                if (!reader.Read())
                    throw new InvalidOperationException("Usuario nao encontrado para envio de confirmacao.");

                nome = reader["NOME"] == DBNull.Value ? login : reader["NOME"].ToString();
                destino = reader["EMAIL"] == DBNull.Value ? objeto.Email : reader["EMAIL"].ToString();
                codigo = CodigoUsuario(objeto.Cnpj, login);
            }

            if (string.IsNullOrWhiteSpace(destino))
                throw new InvalidOperationException("E-mail de confirmacao nao informado.");

            var corpo = $@"
<!DOCTYPE html>
<html lang=""pt-BR"">
<head>
  <meta charset=""UTF-8"">
  <meta name=""viewport"" content=""width=device-width, initial-scale=1.0"">
</head>
<body style=""margin:0;padding:0;background:#f4f4f7;font-family:Arial,sans-serif;"">
  <table width=""100%"" cellpadding=""0"" cellspacing=""0"" style=""background:#f4f4f7;padding:40px 0;"">
    <tr>
      <td align=""center"">
        <table width=""600"" cellpadding=""0"" cellspacing=""0"" style=""background:#ffffff;border-radius:8px;overflow:hidden;box-shadow:0 2px 8px rgba(0,0,0,0.08);"">

          <!-- Cabecalho -->
          <tr>
            <td style=""background:#0f0f1a;padding:32px 40px;text-align:center;"">
              <span style=""font-size:26px;font-weight:bold;color:#ffffff;letter-spacing:1px;"">Syn<span style=""color:#7c6af7;"">Flux</span> PDV</span>
            </td>
          </tr>

          <!-- Corpo -->
          <tr>
            <td style=""padding:40px;color:#333333;"">
              <p style=""margin:0 0 16px;font-size:16px;"">Olá, <strong>{System.Net.WebUtility.HtmlEncode(nome)}</strong>!</p>
              <p style=""margin:0 0 16px;font-size:15px;line-height:1.6;"">
                Obrigado por se cadastrar no <strong>SynFlux PDV</strong>.<br>
                Use o código abaixo para confirmar seu acesso ao sistema:
              </p>

              <!-- Caixa do codigo -->
              <table width=""100%"" cellpadding=""0"" cellspacing=""0"" style=""margin:24px 0;"">
                <tr>
                  <td align=""center"" style=""background:#f0eeff;border-left:4px solid #7c6af7;border-radius:4px;padding:20px;"">
                    <span style=""font-size:20px;font-weight:bold;letter-spacing:6px;color:#0f0f1a;font-family:monospace;"">{System.Net.WebUtility.HtmlEncode(codigo)}</span>
                  </td>
                </tr>
              </table>

              <p style=""margin:0 0 16px;font-size:15px;line-height:1.6;"">
                Informe este código na tela de confirmação do aplicativo para liberar o acesso.
              </p>
              <p style=""margin:0;font-size:13px;color:#888888;"">
                Se você não realizou este cadastro, ignore este e-mail com segurança.
              </p>
            </td>
          </tr>

          <!-- Rodape -->
          <tr>
            <td style=""background:#f4f4f7;padding:24px 40px;text-align:center;border-top:1px solid #eeeeee;"">
              <p style=""margin:0 0 6px;font-size:12px;color:#aaaaaa;"">
                © {DateTime.UtcNow.Year} Synflux &nbsp;|&nbsp;
                <a href=""https://synflux.com.br"" style=""color:#7c6af7;text-decoration:none;"">synflux.com.br</a>
              </p>
              <p style=""margin:0;font-size:11px;color:#bbbbbb;"">Este é um e-mail automático, por favor não responda.</p>
            </td>
          </tr>

        </table>
      </td>
    </tr>
  </table>
</body>
</html>";

            Biblioteca.EnviarEmail("SynFlux PDV - Código de Confirmação", destino, corpo);
        }

        public void ConferirCodigoConfirmacao(Empresa objeto, string codigoConfirmacao)
        {
            ConferirCodigoConfirmacao(objeto, codigoConfirmacao, null);
        }

        public void ConferirCodigoConfirmacao(Empresa objeto, string codigoConfirmacao, string login)
        {
            string codigo = string.IsNullOrWhiteSpace(login)
                ? Biblioteca.MD5String(objeto.Cnpj + Constantes.CHAVE)
                : CodigoUsuario(objeto.Cnpj, login);
            if (codigo != codigoConfirmacao)
            {
                throw new InvalidOperationException("Codigo de confirmacao invalido.");
            }

            using var connection = new MySqlConnection(ObterConnectionString());
            connection.Open();
            GarantirColunasUsuario(connection);

            using var command = new MySqlCommand(@"
                UPDATE EMPRESA
                   SET REGISTRADO = 'S',
                       DATA_REGISTRO = @dataRegistro,
                       HORA_REGISTRO = @horaRegistro
                 WHERE CNPJ = @cnpj", connection);
            command.Parameters.AddWithValue("@dataRegistro", DateTime.Now.Date);
            command.Parameters.AddWithValue("@horaRegistro", Biblioteca.DataParaHora(DateTime.Now));
            command.Parameters.AddWithValue("@cnpj", objeto.Cnpj);

            if (command.ExecuteNonQuery() == 0)
            {
                throw new InvalidOperationException("CNPJ nao encontrado.");
            }

            if (!string.IsNullOrWhiteSpace(login))
            {
                using var userCommand = new MySqlCommand(@"
                    UPDATE RET_USUARIO u
                    JOIN EMPRESA e ON e.ID = u.ID_EMPRESA
                       SET u.CONFIRMADO = 'S',
                           u.CONFIRMADO_EM = UTC_TIMESTAMP()
                     WHERE e.CNPJ = @cnpj
                       AND LOWER(u.LOGIN) = @login", connection);
                userCommand.Parameters.AddWithValue("@cnpj", objeto.Cnpj);
                userCommand.Parameters.AddWithValue("@login", NormalizarLogin(login));

                if (userCommand.ExecuteNonQuery() == 0)
                    throw new InvalidOperationException("Usuario nao encontrado para confirmacao.");
            }
        }        public void Excluir(Empresa objeto)
        {
            using (ISession Session = NHibernateHelper.GetSessionFactory().OpenSession())
            {
                NHibernateDAL<Empresa> DAL = new NHibernateDAL<Empresa>(Session);
                DAL.Delete(objeto);
                Session.Flush();
            }
        }
        
    }
}
