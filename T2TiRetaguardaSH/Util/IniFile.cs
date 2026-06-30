using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;

namespace T2TiRetaguardaSH.Util
{
    public class IniFile
    {
        private string FileName { set; get; }
        private readonly string section = Path.GetFileNameWithoutExtension(Environment.CurrentDirectory);
        private static readonly bool IsWindows = RuntimeInformation.IsOSPlatform(OSPlatform.Windows);

        // --- API do Windows ---
        [DllImport("kernel32", CharSet = CharSet.Unicode)]
        private static extern long WritePrivateProfileString(string section, string key, string val, string filePath);

        [DllImport("kernel32", CharSet = CharSet.Unicode)]
        private static extern int GetPrivateProfileString(string section, string key, string def,
            StringBuilder retVal, int size, string filePath);

        // ===================== CONSTRUTORES =====================

        // Construtor com 1 argumento (arquivo completo ou nome simples)
        public IniFile(string fileName)
        {
            if (fileName.Contains(Path.DirectorySeparatorChar.ToString()) || fileName.Contains("/"))
                FileName = fileName;
            else
                FileName = Path.Combine(Environment.CurrentDirectory, fileName);
        }

        // Construtor com 2 argumentos (path + nome do arquivo)
        public IniFile(string path, string fileName)
        {
            if (!path.EndsWith(Path.DirectorySeparatorChar))
                path += Path.DirectorySeparatorChar;
            FileName = Path.Combine(path, fileName);
        }

        // Construtor sem argumentos (usa config.ini na pasta atual)
        public IniFile() : this(Path.Combine(Environment.CurrentDirectory, "config.ini")) { }

        // ===================== WINDOWS MODE =====================
        private string ReadWin(string section, string key, string def)
        {
            StringBuilder temp = new StringBuilder(255);
            GetPrivateProfileString(section, key, def, temp, 255, FileName);
            return temp.ToString();
        }

        private void WriteWin(string section, string key, string value)
        {
            WritePrivateProfileString(section, key, value, FileName);
        }

        // ===================== LINUX / MAC MODE =====================
        private Dictionary<string, Dictionary<string, string>> LoadIni()
        {
            var data = new Dictionary<string, Dictionary<string, string>>(StringComparer.OrdinalIgnoreCase);
            if (!File.Exists(FileName)) return data;

            string currentSection = "";
            foreach (var line in File.ReadAllLines(FileName))
            {
                string trimmed = line.Trim();
                if (trimmed.StartsWith(";") || trimmed == "") continue;

                if (trimmed.StartsWith("[") && trimmed.EndsWith("]"))
                {
                    currentSection = trimmed.Substring(1, trimmed.Length - 2);
                    if (!data.ContainsKey(currentSection))
                        data[currentSection] = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                }
                else if (trimmed.Contains("="))
                {
                    var parts = trimmed.Split('=', 2);
                    if (!data.ContainsKey(currentSection))
                        data[currentSection] = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

                    data[currentSection][parts[0].Trim()] = parts[1].Trim();
                }
            }
            return data;
        }

        private void SaveIni(Dictionary<string, Dictionary<string, string>> data)
        {
            var sb = new StringBuilder();
            foreach (var sec in data)
            {
                sb.AppendLine($"[{sec.Key}]");
                foreach (var kv in sec.Value)
                    sb.AppendLine($"{kv.Key}={kv.Value}");
                sb.AppendLine();
            }
            File.WriteAllText(FileName, sb.ToString());
        }

        // ===================== PUBLIC METHODS =====================

        public string IniReadString(string Section, string Key, string def = "")
        {
            if (IsWindows)
                return ReadWin(Section, Key, def);

            var ini = LoadIni();
            return ini.ContainsKey(Section) && ini[Section].ContainsKey(Key)
                ? ini[Section][Key]
                : def;
        }

        public string IniReadString(string Key, string def = "")
            => IniReadString(section, Key, def);

        public int IniReadInt(string Section, string Key, int def = 0)
        {
            if (int.TryParse(IniReadString(Section, Key, def.ToString()), out int val))
                return val;
            return def;
        }

        public int IniReadInt(string Key, int def = 0)
            => IniReadInt(section, Key, def);

        public bool IniReadBool(string Section, string Key, bool def = false)
        {
            if (bool.TryParse(IniReadString(Section, Key, def.ToString()), out bool val))
                return val;
            return def;
        }

        public bool IniReadBool(string Key, bool def = false)
            => IniReadBool(section, Key, def);

        public void IniWriteString(string Section, string Key, string value)
        {
            if (IsWindows)
            {
                WriteWin(Section, Key, value);
                return;
            }

            var ini = LoadIni();
            if (!ini.ContainsKey(Section))
                ini[Section] = new Dictionary<string, string>();

            ini[Section][Key] = value;
            SaveIni(ini);
        }

        public void IniWriteString(string Key, string value)
            => IniWriteString(section, Key, value);

        public void IniWriteInt(string Section, string Key, int value)
            => IniWriteString(Section, Key, value.ToString());

        public void IniWriteInt(string Key, int value)
            => IniWriteInt(section, Key, value);

        public void IniWriteBool(string Section, string Key, bool value)
            => IniWriteString(Section, Key, value.ToString());

        public void IniWriteBool(string Key, bool value)
            => IniWriteBool(section, Key, value);
    }
}
