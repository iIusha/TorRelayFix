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
using System.Net.Http;
using System.Threading;

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
            var random = new Random();
            relays = relays.OrderBy(x => random.Next()).ToDictionary(y => y.Key, y => y.Value);
            Dictionary<string, string> workingRelays = new Dictionary<string, string>();
            int counter = 0;
            int BATCH_SIZE = 200;
            do
            {
                counter += BATCH_SIZE;
                relays = Pop(relays, out Dictionary<string, string> new_relays, BATCH_SIZE);
                var temp = FetchRelays(new_relays).GetAwaiter().GetResult();
                if (workingRelays.Count > 0)
                    foreach (var item in temp)
                        workingRelays.Add(item.Key, item.Value);
                else workingRelays = temp;
                Console.WriteLine($"WORKING RELAYS FOUND TOTAL: {workingRelays.Count}/5. Adresses scanned: {counter}");

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
            var relays = hostsFingerprints.Select(x => GetAsync($"https://{x.Key}/"));
            var results = await Task.WhenAll(relays);

            Dictionary<string, string> workingRelays = new Dictionary<string, string>();

            for (int i = 0; i < results.Length; i++)
                if (results.ElementAt(i))
                    workingRelays.Add(hostsFingerprints.ElementAt(i).Key, hostsFingerprints.ElementAt(i).Value);

            return workingRelays;            
        }

        static async Task<bool> GetAsync(string uri)
        {
            int[] ports = { 1, 7, 9, 11, 13, 15, 17, 19, 20, 21, 22, 23, 25, 37, 42, 43, 53, 69, 77, 79, 87,
                95, 101, 102, 103, 104, 109, 110, 111, 113, 115, 117, 119, 123, 135, 137, 139, 143, 161, 179,
                389, 427, 465, 512, 513, 514, 515, 526, 530, 531, 532, 540, 548, 554, 556, 563, 587, 601, 636,
                989, 990, 993, 995, 1719, 1720, 1723, 2049, 3659, 4045, 5060, 5061, 6000, 6566, 6665, 6666,
                6667, 6668, 6669, 6697, 10080 };
            var port = int.Parse(uri.Split(':')[2].Split('/')[0]);
            if (ports.Contains(port)) return false;
            var handler = new TimeoutHandler
            {
                DefaultTimeout = TimeSpan.FromSeconds(5),
                InnerHandler = new HttpClientHandler()
            };

            using (var cts = new CancellationTokenSource())
            using (var client = new HttpClient(handler))
            {
                client.Timeout = Timeout.InfiniteTimeSpan;

                var request = new HttpRequestMessage(HttpMethod.Get, uri);
                try
                {
                    using (var response = await client.SendAsync(request, cts.Token))
                    {
                        Console.WriteLine($"{uri} response: {response.StatusCode}");
                    }
                }
                
                catch (HttpRequestException ex) when (ex.InnerException is WebException)
                {
                    if ((ex.InnerException as WebException).Status == WebExceptionStatus.ConnectFailure)
                        return false;
                }
                catch (HttpRequestException ex)
                {
                    Console.WriteLine(ex);
                }
                catch (TimeoutException)
                {
                    return false;
                }
                Console.WriteLine($"{uri} - GOOD");
                return true;
            }
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

    public static class HttpRequestExtensions
    {
        private const string TimeoutPropertyKey = "RequestTimeout";

        public static void SetTimeout(this HttpRequestMessage request, TimeSpan? timeout)
        {
            if (request == null) throw new ArgumentNullException(nameof(request));
            request.Properties[TimeoutPropertyKey] = timeout;
        }

        public static TimeSpan? GetTimeout(this HttpRequestMessage request)
        {
            if (request == null) throw new ArgumentNullException(nameof(request));
            if (request.Properties.TryGetValue(TimeoutPropertyKey, out var value) && value is TimeSpan timeout)
                return timeout;
            return null;
        }
    }

    public class TimeoutHandler : DelegatingHandler
    {
        public TimeSpan DefaultTimeout { get; set; } = TimeSpan.FromSeconds(100);

        protected async override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            using (var cts = GetCancellationTokenSource(request, cancellationToken))
            {
                try
                {
                    return await base.SendAsync(request, cts?.Token ?? cancellationToken);
                }
                catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
                {
                    throw new TimeoutException();
                }
            }
        }

        private CancellationTokenSource GetCancellationTokenSource(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var timeout = request.GetTimeout() ?? DefaultTimeout;
            if (timeout == Timeout.InfiniteTimeSpan)
            {
                return null;
            }
            else
            {
                var cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                cts.CancelAfter(timeout);
                return cts;
            }
        }
    }
}
