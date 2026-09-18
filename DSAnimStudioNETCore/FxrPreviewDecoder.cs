using System;
using System.Diagnostics;
using System.IO;
using System.Security.Cryptography;
using System.Threading;
using System.Threading.Tasks;
using Newtonsoft.Json.Linq;
using SoulsAssetPipeline;

namespace DSAnimStudio
{
    public static class FxrPreviewDecoder
    {
        static readonly SemaphoreSlim workers=new(2);
        // The Node runtime is a ~90 MB third-party binary and is intentionally not
        // redistributed with the source tree. Resolve it in this order: a copy
        // placed beside the scripts, an explicit DSA_FXR_NODE override, then
        // whatever Node the machine already exposes on PATH.
        static string ResolveNode(string runtime)
        {
            string script=Path.Combine(runtime,"decode.mjs");
            if(!File.Exists(script))throw new FileNotFoundException("FXR decoding needs Res/FxrRuntime/decode.mjs beside the application.",script);
            string bundled=Path.Combine(runtime,OperatingSystem.IsWindows()?"node.exe":"node");
            if(File.Exists(bundled))return bundled;
            string configured=Environment.GetEnvironmentVariable("DSA_FXR_NODE");
            if(!string.IsNullOrWhiteSpace(configured)&&File.Exists(configured))return configured;
            return "node";
        }
        public static string GameName(SoulsGames game)=>game switch
        { SoulsGames.ER=>"ER", SoulsGames.ERNR=>"NR", SoulsGames.DS3=>"DS3", SoulsGames.SDT=>"SDT", SoulsGames.AC6=>"AC6", _=>null };
        public static async Task<JObject> Decode(byte[] source, SoulsGames game, CancellationToken cancellation)
        {
            string gameName=GameName(game)??throw new NotSupportedException("FXR decoding supports ER, NR, DS3, Sekiro and AC6. This game's FXR format is not implemented.");
            if(source.Length>32*1024*1024)throw new InvalidDataException("FXR exceeds the 32 MiB decode budget.");
            string hash=Convert.ToHexString(SHA256.HashData(source));
            string revision=gameName=="SDT"?"32.1.0-v4":"32.1.0-v3";
            string cache=Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),"DSAnimStudio","FxrCache",revision,gameName,hash+".json");
            JObject ReadCached()
            {
                try
                {
                    if(!File.Exists(cache))return null;
                    var p=JObject.Parse(File.ReadAllText(cache));
                    return (string)p["SourceSha256"]==hash && (string)p["Game"]==gameName && p["Root"] is JObject?p:null;
                }
                catch(IOException) { return null; }
                catch(Newtonsoft.Json.JsonException) { return null; }
            }
            var cached=ReadCached();if(cached!=null)return cached;
            await workers.WaitAsync(cancellation);
            try
            {
                cached=ReadCached();if(cached!=null)return cached;
                string runtime=Path.Combine(AppContext.BaseDirectory,"Res","FxrRuntime");
                var start=new ProcessStartInfo(ResolveNode(runtime))
                { UseShellExecute=false,CreateNoWindow=true,RedirectStandardInput=true,RedirectStandardOutput=true,RedirectStandardError=true };
                start.ArgumentList.Add("--max-old-space-size=256");start.ArgumentList.Add(Path.Combine(runtime,"decode.mjs"));start.ArgumentList.Add(gameName);
                using var process=new Process{StartInfo=start};
                try
                {
                    if(!process.Start())throw new IOException("Unable to start the FXR decoder.");
                }
                catch(System.ComponentModel.Win32Exception ex)
                {
                    throw new InvalidOperationException("FXR decoding needs a Node.js runtime. Place node beside Res/FxrRuntime, set DSA_FXR_NODE, or install Node so that \"node\" is on PATH.",ex);
                }
                using var timeout=CancellationTokenSource.CreateLinkedTokenSource(cancellation);timeout.CancelAfter(TimeSpan.FromSeconds(30));
                using var registration=timeout.Token.Register(()=>{try{if(!process.HasExited)process.Kill(true);}catch(InvalidOperationException){} });
                var output=process.StandardOutput.ReadToEndAsync(timeout.Token);
                var error=process.StandardError.ReadToEndAsync(timeout.Token);
                await process.StandardInput.BaseStream.WriteAsync(source,timeout.Token);process.StandardInput.Close();
                await process.WaitForExitAsync(timeout.Token);
                string json=await output, diagnostic=await error;
                if(process.ExitCode!=0)throw new InvalidDataException("FXR decoder rejected this resource: "+diagnostic.Trim()[..Math.Min(240,diagnostic.Trim().Length)]);
                var profile=JObject.Parse(json);
                if(profile["Root"] is not JObject)throw new InvalidDataException("Decoded FXR has no root.");
                profile["Game"]=gameName;profile["SourceSha256"]=hash;
                try
                {
                    Directory.CreateDirectory(Path.GetDirectoryName(cache));string temporary=cache+"."+Guid.NewGuid().ToString("N")+".tmp";
                    try { File.WriteAllText(temporary,profile.ToString(Newtonsoft.Json.Formatting.None));File.Move(temporary,cache,true); }
                    finally { if(File.Exists(temporary))File.Delete(temporary); }
                }
                catch(IOException) { /* A read-only cache must not disable preview. */ }
                catch(UnauthorizedAccessException) { }
                return profile;
            }
            finally { workers.Release(); }
        }
    }
}
