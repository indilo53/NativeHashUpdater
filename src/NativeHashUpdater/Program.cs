using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using System.ComponentModel;
using System.Diagnostics;
using System.IO.Compression;
using System.Reflection;
using System.Text.RegularExpressions;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace NativeHashUpdater
{
    class Program
    {
        private static string[] stringArgs;

        private static readonly ManualResetEvent mre = new ManualResetEvent(false);

        private static readonly BackgroundWorker worker = new BackgroundWorker();

        class HashStringPair
        {
            public HashStringPair(string oldHash, string newHash)
            {
                OriginalHash = oldHash;
                NewHash = newHash;
            }

            public readonly string OriginalHash;
            public readonly string NewHash;
        }

        static void Main(string[] args)
        {
            if (args.Length < 1)
                throw new ArgumentException("Arguments cannot be null.");

            stringArgs = args;

            worker.DoWork += DoWork;
            worker.RunWorkerCompleted += RunWorkerCompleted;
            worker.RunWorkerAsync();

            mre.Reset();

            mre.WaitOne();

            Thread.Sleep(1000);
        }

        private static void DoWork(object sender, DoWorkEventArgs e)
        {
            List<HashStringPair[]> fileList = new List<HashStringPair[]>();

            var fileNames = stringArgs.Select(Path.GetFileName);

            var files = fileNames.OrderBy(x => Convert.ToUInt32(string.Concat(x.Skip(1).TakeWhile(y => y != '-')))).ToArray();

            for (int i = 0; i < files.Length; i++)
            {
                var hashPairs = new List<HashStringPair>();

                using (StreamReader reader = new StreamReader(AppDomain.CurrentDomain.BaseDirectory + "//" + files[i]))
                {
                    string line;
                    while ((line = reader.ReadLine()) != null)
                    {
                        int idx = line.IndexOf('{') + 2;
                        int idx2 = line.IndexOf(',');

                        string oldHash = line.Substring(idx, idx2 - idx);

                        idx = idx2 + 2;

                        string newHash = line.Substring(idx, (line.IndexOf('}') - 1) - idx);

                        hashPairs.Add(new HashStringPair(oldHash, newHash));
                    }

                    fileList.Add(hashPairs.ToArray());
                }
            }

            var startFile = fileList.First();

            Console.WriteLine("Found " + startFile.Length + " natives in " + files.First());

            List<HashStringPair> newHashes = new List<HashStringPair>();

            Console.WriteLine("Searching for updates hash values...");

            for (int i = 0; i < startFile.Length; i++)
            {
                string newHash = startFile[i].NewHash;

                string originalHash = startFile[i].OriginalHash;             

                for (int x = 1; x < fileList.Count; x++)
                {
                    var foundHashPair = fileList[x].FirstOrDefault(y => y.OriginalHash == newHash);

                    if (foundHashPair != null)
                    {
                        newHash = foundHashPair.NewHash;
                    }               
                }

                if (newHash != originalHash)
                {
                    newHashes.Add(new HashStringPair(originalHash, newHash));
                }
            }

            Console.WriteLine("Found " + newHashes.Count + " matches.");

            if (!File.Exists("natives.json"))
                Console.WriteLine("[ERROR] natives.json could not be found.");

            Console.WriteLine("Writing x64natives.dat / HashMapData.h");

            using (StreamReader file = File.OpenText("natives.json"))
            using (JsonTextReader reader = new JsonTextReader(file))
            using (StreamWriter writer1 = new StreamWriter("x64natives.dat"))
            using (StreamWriter writer2 = new StreamWriter("HashMapData.h"))
            {
                JObject JSONNAtives = (JObject)JToken.ReadFrom(reader);

                foreach (var elem in JSONNAtives)
                {
                    var ns = elem.Key;
                    var natives = (JObject)elem.Value;

                    foreach (var infos in natives)
                    {
                        var hash = infos.Key;
                        var data = (JObject)infos.Value;
                        var name = (string)data["name"];

                        string def;

                        if (name == "")
                        {
                            def = hash + ":" + ns + ":_" + hash;
                        }
                        else
                        {
                            def = hash + ":" + ns + ":" + name;
                        }

                        writer1.Write(def + "\n");

                    }
                }

                foreach (var elem in JSONNAtives)
                {
                    var ns = elem.Key;
                    var natives = (JObject)elem.Value;

                    foreach (var infos in natives)
                    {
                        var hash = infos.Key;
                        var data = (JObject)infos.Value;
                        var name = (string)data["name"];

                        string def;

                        if (name == "")
                        {
                            def = hash + ":" + ns + ":_" + hash;
                        }
                        else
                        {
                            def = hash + ":" + ns + ":" + name;
                        }

                        writer2.Write(def + "\n");

                    }
                }
            }

            if (!File.Exists("natives.h"))
                Console.WriteLine("[ERROR] natives.h header could not be found.");


            string[] headerLines = File.ReadAllLines("natives.h");

            for (int i = 0; i < headerLines.Length; i++)
            {
                string s = headerLines[i];

                if (!s.StartsWith("\tstatic")) continue;

                string hashStr = s.Substring(s.IndexOf("0x", StringComparison.Ordinal), 18);

                var builder = new StringBuilder(s);

                HashStringPair pair;

                if ((pair = newHashes.FirstOrDefault(x => x.OriginalHash == hashStr)) != null)
                {
                    int idx = headerLines[i].IndexOf('{');
                    headerLines[i] = builder.Replace(hashStr, pair.NewHash, idx, headerLines[i].IndexOf('}') - idx).ToString();
                }
            }

            string filename = Path.GetFileNameWithoutExtension(files.Last());

            filename = filename.Substring(filename.IndexOf('-') + 1);

            string outputFilename = "natives-" + filename + ".h";

            Console.WriteLine("Writing " + outputFilename + "...");

            File.WriteAllLines(outputFilename, headerLines); // write new header   

            Console.WriteLine("Generating Crossmap...");

            GenerateCrossmap(newHashes);

            Console.WriteLine("Generating Translation...");

            GenerateTranslation();

            string peAddrMapFile = "addresses-" + filename + ".txt";

            if (File.Exists(peAddrMapFile))
            {
                Console.WriteLine("Generating IDC...");
                GenerateIDC(headerLines, File.ReadAllLines(peAddrMapFile), "addresses-" + filename + ".idc");
            }
        }

        private static void GenerateCrossmap(IEnumerable<HashStringPair> pairs)
        {
            using (StreamWriter writer = new StreamWriter("crossmap.txt"))
            {
                foreach (var item in pairs)
                {
                    writer.WriteLine($"{{{item.OriginalHash}, {item.NewHash}}},");
                }
            }
        }

        private static void GenerateTranslation()
        {

            using (FileStream outStream = File.Create("native_translation.dat"))
            {
                using (DeflateStream inflate = new DeflateStream(outStream, CompressionMode.Compress))
                {
                    string[] files = Directory.GetFiles(Path.GetDirectoryName(Assembly.GetExecutingAssembly().Location), "b*-b*.txt").OrderBy(e => Convert.ToUInt32(string.Concat(e.Split('\\').Last().Skip(1).TakeWhile(x => x != '-')))).ToArray();

                    for (int i = 0; i < files.Length; i++)
                    {
                        using (StreamReader reader = new StreamReader(files[i]))
                        {
                            string line;

                            while ((line = reader.ReadLine()) != null)
                            {
                                var split = Regex.Split(line, @"\{(.*),(.*)\}");
                                string from = split[1].Replace(" ", "").Replace("0x", "");
                                string to = split[2].Replace(" ", "").Replace("0x", "");

                                try
                                {

                                    if (from == to || ulong.Parse(from, System.Globalization.NumberStyles.HexNumber) == 0)
                                    {
                                        continue;
                                    }
                                }
                                catch (Exception e)
                                {
                                    continue;
                                }


                                string l = $"{to}:{from}\n";
                                byte[] data = Encoding.Default.GetBytes(l);

                                inflate.Write(data, 0, data.Length);
                            }

                            inflate.Flush();
                        }
                    }
                }
            }
        }


        private static void RunWorkerCompleted(object sender, RunWorkerCompletedEventArgs e)
        {
            Console.WriteLine("Done!");
            mre.Set();
        }

        private static void GenerateIDC(string[] nativeHeader, string[] nativeAddresses, string outputFilename)
        {
            using (StreamWriter file = new StreamWriter(outputFilename))
            {
                file.Write("\n#include <idc.idc>\n\nstatic main() {\n");

                for (int i = 0; i < nativeHeader.Length; i++)
                {
                    string s = nativeHeader[i];

                    if (!s.Contains("\tstatic")) continue;

                    string hashStr = s.Substring(s.IndexOf("0x", StringComparison.Ordinal), 18);

                    string nativeName = s.Split(' ')[2];

                    nativeName = nativeName.Substring(0, nativeName.IndexOf('('));

                    Parallel.For(0, nativeAddresses.Length, x => {
                        if (!string.IsNullOrWhiteSpace(nativeAddresses[x]))
                        {
                            string[] currentLine = nativeAddresses[x].Split(':');

                            string hashSubStr = currentLine[0].TrimEnd();

                            string address = currentLine[1].TrimStart();

                            if (hashStr == hashSubStr)
                            {
                                file.WriteLine($"\tMakeName({address}, \"{nativeName}\");");
                            }
                        }
                    });
                }

                file.WriteLine("}\n");
            }
        }
    }
}
