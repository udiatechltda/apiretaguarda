using NHibernate;
using NHibernate.Cfg;
using System;

namespace T2TiRetaguardaSH.NHibernate
{
    public class NHibernateHelper
    {
        private static ISessionFactory SessionFactory;

        public static ISessionFactory GetSessionFactory()
        {
            try
            {
                if (SessionFactory == null)
                {
                    lock (typeof(NHibernateHelper))
                    {
                        Console.WriteLine("--> [DEBUG] Iniciando configuracao do NHibernate...");
                        
                        Configuration config = new Configuration();
                        
                        // Tenta ler o hibernate.cfg.xml
                        config.Configure();
                        Console.WriteLine("--> [DEBUG] Arquivo hibernate.cfg.xml lido com sucesso.");
                        
                        config.AddAssembly("T2TiRetaguardaSH");
                        
                        SessionFactory = config.BuildSessionFactory();
                        Console.WriteLine("--> [DEBUG] Sessao (SessionFactory) criada com sucesso!");
                    }
                }
                return SessionFactory;
            }
            catch (Exception ex)
            {
                // ISSO VAI MOSTRAR O ERRO REAL NO LOG DO DOCKER
                Console.WriteLine("!!! ERRO CRÍTICO NO NHIBERNATE !!!");
                Console.WriteLine("Mensagem: " + ex.Message);
                if (ex.InnerException != null)
                {
                    Console.WriteLine("Motivo Real (Inner Exception): " + ex.InnerException.Message);
                    Console.WriteLine("StackTrace Inner: " + ex.InnerException.StackTrace);
                }
                else 
                {
                     Console.WriteLine("StackTrace: " + ex.StackTrace);
                }
                
                // Joga o erro para frente para a API saber que falhou
                throw; 
            }
        }
    }
}
