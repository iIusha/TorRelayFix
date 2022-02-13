using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using Microsoft.Win32;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using System.Runtime.InteropServices;
using System.Diagnostics;

namespace TorRelayFix
{
    class Program
    {
        [DllImport("user32.dll")]
        static extern bool ShowWindow(IntPtr hWnd, int nCmdShow);
        static void Main(string[] args)
        {
            const string ONION_RELAYS = "https://corsanywhere.herokuapp.com/https://onionoo.torproject.org/details?type=relay&running=true&fields=fingerprint,or_addresses";
            string json;
            string torPath = "";
            bool silent = false;

            for (int i = 0; i < args.Length; i++)
            {
                switch(args[i])
                {
                    case "--silent":
                    case "-s":
                        silent = true;
                        break;
                    case "--tor-folder":
                    case "-f":
                        torPath = args[i+1];
                        if(!Directory.Exists(torPath))
                        {
                            Log("Specified directory not found: " + $"'{torPath}'", silent);
                            Log("Exiting...", silent);
                            if (!silent) Console.ReadKey();
                            return;
                        }
                        break;
                    default:
                        break;
                }
            }

            if (silent)
            {
                IntPtr h = Process.GetCurrentProcess().MainWindowHandle;
                ShowWindow(h, 0);
            }

            if (torPath.Length <= 0)
            {
                Log("Tor Browser path was not specified, trying to find...", silent);
                torPath = TryFindTor();
            }
            if (torPath == null)
            {
                Log("Tor Browser folder not found, please specify (e.g program.exe --tor-folder \"F:\\Programs\\Tor Browser\\firefox.exe\")", silent);
                if (!silent) Console.ReadKey();
                return;
            }

            Log($"Tor Browser path: {torPath}", silent);
            Log("Requesting Relays...", silent);
            var request = WebRequest.Create(ONION_RELAYS);
            using (HttpWebResponse response = (HttpWebResponse)request.GetResponse())
            using (Stream stream = response.GetResponseStream())
            using (StreamReader reader = new StreamReader(stream))
            {
                json = reader.ReadToEnd();
            }
            var relays = Parse(json);
            Log("Got Relays: " + relays.Keys.Count, silent);
            Log("Fetching...", silent);
            Dictionary<string, string> workingRelays = new Dictionary<string, string>();
            do
            {
                relays = Pop(relays, out Dictionary<string, string> new_relays, 30);
                var temp = FetchRelays(new_relays).GetAwaiter().GetResult();
                if (workingRelays.Count > 0)
                    foreach (var item in temp)
                        workingRelays.Add(item.Key, item.Value);
                else workingRelays = temp;

            } while (workingRelays.Count < 5);

            Log($"Got response from {workingRelays.Count} relays:", silent);

            foreach (var item in workingRelays)
                Log($"\taddress: {item.Key},\tfingerprint: {item.Value}", silent);

            Log("Adding to Tor...", silent);

            var success = PatchSettings(torPath, workingRelays);

            if (success) Log("Done. Press any button to close window...", silent);

            else Log("Something went wrong while adding relays to settings.\n You can add it manually in  'Provide a Bridge' (about:preferences#tor)", silent);

            if (!silent) Console.ReadKey();
        }


        public static bool PatchSettings(string path, Dictionary<string, string> relays)
        {
            try
            {
                if (path.EndsWith(".exe")) path = path.Substring(0, path.LastIndexOf('\\'));
                if (path.EndsWith("\\")) path = path.Substring(0, path.Length - 1);

                string browserDir = path + @"\TorBrowser\Data\Browser";
                string[] files = Directory.GetFiles(browserDir, "prefs.js", SearchOption.AllDirectories);

                foreach (var file in files)
                {
                    string text = File.ReadAllText(file);
                    if (!File.Exists(file + ".bak"))
                    {
                        File.Copy(file, file + ".bak");
                    }
                    if (text.Contains("torbrowser.settings"))
                    {
                        string tempFile = Path.GetTempFileName();

                        using (var sr = new StreamReader(file))
                        using (var sw = new StreamWriter(tempFile))
                        {
                            string line;

                            while ((line = sr.ReadLine()) != null)
                            {
                                if (!line.Contains("torbrowser.settings.bridges.bridge_strings") &&
                                    !line.Contains("torbrowser.settings.bridges.enabled") &&
                                    !line.Contains("torbrowser.settings.bridges.source"))
                                    sw.WriteLine(line);
                            }

                            for (int i = 0; i < relays.Count; i++)
                            {
                                var item = relays.ElementAt(i);
                                sw.WriteLine($"user_pref(\"torbrowser.settings.bridges.bridge_strings.{i}\", \"{item.Key} {item.Value}\");");
                            }
                            sw.WriteLine("user_pref(\"torbrowser.settings.bridges.enabled\", true);");
                            sw.WriteLine("user_pref(\"torbrowser.settings.bridges.source\", 2);");
                        }

                        File.Delete(file);
                        File.Move(tempFile, file);
                    }
                }
                return true;
            }
            catch
            {
                Console.WriteLine("fuck");
                return false;
            }
        }

        public static void Log(string text, bool s)
        {
            if (!s) Console.WriteLine(text);
        }
        public static string TryFindTor()
        {
            var key = Registry.CurrentUser.OpenSubKey("Software\\Mozilla\\Firefox\\Launcher");
            if (key == null) return null;
            foreach (var item in key.GetValueNames())
                if (item.Contains("Tor Browser")) return item.Split('|')[0];

            return null;
        }

        public static Dictionary<string, string> Pop(Dictionary<string, string> dict, out Dictionary<string, string> new_dict, int count)
        {
            new_dict = dict.Take(count).ToDictionary(x => x.Key, y => y.Value);
            foreach (var item in new_dict)
                dict.Remove(item.Key);

            return dict;
        }

        public static async Task<Dictionary<string, string>> FetchRelays(Dictionary<string, string> hostsFingerprints)
        {
            var relays = hostsFingerprints.Select(x => Get($"https://{x.Key}/"));
            var results = await Task.WhenAll(relays);

            Dictionary<string, string> workingRelays = new Dictionary<string, string>();

            for (int i = 0; i < results.Length; i++)
                if (results.ElementAt(i))
                    workingRelays.Add(hostsFingerprints.ElementAt(i).Key, hostsFingerprints.ElementAt(i).Value);

            return workingRelays;            
        }

        public static async Task<bool> Get(string uri)
        {
            HttpWebRequest request = (HttpWebRequest)WebRequest.Create(uri);
            request.AutomaticDecompression = DecompressionMethods.GZip | DecompressionMethods.Deflate;
            try
            {
                HttpWebResponse response = (HttpWebResponse) await request.GetResponseAsync();
            }
            catch (WebException ex)
            {
                if (ex.Status == WebExceptionStatus.Timeout || ex.Status == WebExceptionStatus.ConnectFailure)
                    return false;
            }
            return true;
        }

        public static Dictionary<string, string> Parse(string json)
        {
            Dictionary<string, string> relays = new Dictionary<string, string>();
            foreach(var line in Regex.Split(json, "\r\n|\r|\n"))
            {
                var fp = Regex.Match(line, "\"(?<Key>.*?)\":\"(?<Value>.*?)\"");
                var addr = Regex.Match(line, "\\,\"(?<Key>.*?)\":\\[\"(?<Value>.*?)\"");
                if (fp.Success && addr.Success)
                    if (!relays.ContainsKey(addr.Groups["Value"].Value))
                    relays.Add(addr.Groups["Value"].Value, fp.Groups["Value"].Value); 
            }
            return relays;
        }
    }
}
