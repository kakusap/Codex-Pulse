using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Net;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;

namespace CodexPulse {
    public sealed class UpdateRelease {
        public string VersionText,Notes,PageUrl,PackageName,PackageUrl,ChecksumUrl,Digest;
        public Version Version;
        public long Size;
    }
    public sealed class PreparedUpdate {
        public string Directory,Executable,Icon,Version;
    }
    public sealed class UpdateAssetDto {
        public string name, browser_download_url, digest, state;
        public long size;
    }
    public sealed class UpdateReleaseDto {
        public string tag_name,body,html_url;
        public bool draft,prerelease;
        public UpdateAssetDto[] assets;
    }
    public sealed class AppUpdater {
        public const string ReleasesUrl="https://github.com/kakusap/Codex-Pulse/releases";
        internal const long PackageLimit=64*1024*1024;
        public static Version CurrentVersion { get { return typeof(AppUpdater).Assembly.GetName().Version; } }
        public static string CurrentVersionText { get { return CurrentVersion.ToString(3); } }
        public static string FriendlyError(Exception error) {
            if(error is OperationCanceledException) return "更新已取消，当前版本保持不变。";
            var web=error as WebException;
            if(web!=null) {
                var response=web.Response as HttpWebResponse;
                if(response!=null && (response.StatusCode==HttpStatusCode.Forbidden || (int)response.StatusCode==429)) return "GitHub 查询限流，请稍后重试或前往发布页下载。";
                return "无法连接 GitHub，请检查网络后重试，或前往发布页下载。";
            }
            if(error is UnauthorizedAccessException) return "程序目录没有写入权限，请将程序放在可写目录，或从发布页手动更新。";
            return error.Message;
        }
        internal static bool SameVersion(Version first,Version second) {
            return first.Major==second.Major && first.Minor==second.Minor && Math.Max(0,first.Build)==Math.Max(0,second.Build) && Math.Max(0,first.Revision)==Math.Max(0,second.Revision);
        }
        internal static UpdateRelease Parse(string json) {
            var data=Json.New().Deserialize<UpdateReleaseDto>(json);
            Version version;
            if(data==null || data.draft || data.prerelease || data.tag_name==null ||
                !Regex.IsMatch(data.tag_name,@"^v\d+\.\d+\.\d+$") || !Version.TryParse(data.tag_name.Substring(1),out version)) throw new IOException("发布版本信息无效。请前往发布页查看。");
            string name="CodexPulse-"+data.tag_name+"-windows-x64.zip";
            var assets=data.assets??new UpdateAssetDto[0];
            var packages=assets.Where(a=>a.name==name).ToArray();
            var checksums=assets.Where(a=>a.name=="SHA256SUMS.txt").ToArray();
            if(packages.Length!=1 || checksums.Length!=1 || packages[0].state!="uploaded" || checksums[0].state!="uploaded" || packages[0].size<=0 || packages[0].size>PackageLimit)
                throw new IOException("新版本缺少完整的 Windows x64 运行包或校验文件，请稍后重试。");
            string prefix=ReleasesUrl+"/download/"+data.tag_name+"/";
            if(packages[0].browser_download_url!=prefix+name || checksums[0].browser_download_url!=prefix+"SHA256SUMS.txt" || data.html_url!=ReleasesUrl+"/tag/"+data.tag_name)
                throw new IOException("更新来源不属于官方发布仓库，已停止更新。");
            return new UpdateRelease {Version=version,VersionText=version.ToString(3),Notes=data.body??"",PageUrl=data.html_url,PackageName=name,PackageUrl=packages[0].browser_download_url,ChecksumUrl=checksums[0].browser_download_url,Size=packages[0].size,Digest=packages[0].digest};
        }
        public async Task<UpdateRelease> CheckAsync(CancellationToken token) {
            byte[] bytes=await Download("https://api.github.com/repos/kakusap/Codex-Pulse/releases/latest",2*1024*1024,null,null,token);
            return Parse(Encoding.UTF8.GetString(bytes));
        }
        // No credentials or account metadata are attached to these public requests.
        internal static async Task<byte[]> Download(string url,long limit,string destination,IProgress<int> progress,CancellationToken token) {
            ServicePointManager.SecurityProtocol|=SecurityProtocolType.Tls12;
            var request=(HttpWebRequest)WebRequest.Create(url);
            request.UserAgent="CodexPulse/"+CurrentVersionText;request.Accept="application/vnd.github+json";
            request.Timeout=30000;request.ReadWriteTimeout=30000;request.MaximumAutomaticRedirections=8;
            using(var deadline=CancellationTokenSource.CreateLinkedTokenSource(token)) {
                deadline.CancelAfter(destination==null?30000:180000);
                using(deadline.Token.Register(request.Abort)) {
                    try {
                        using(var response=(HttpWebResponse)await request.GetResponseAsync().ConfigureAwait(false)) {
                            string host=response.ResponseUri.Host;
                            if(response.ResponseUri.Scheme!="https" || !(host=="api.github.com" || host=="github.com" || host.EndsWith(".githubusercontent.com",StringComparison.OrdinalIgnoreCase))) throw new IOException("更新下载跳转到了非官方地址。");
                            if(response.ContentLength>limit) throw new IOException("更新文件超过大小限制。");
                            using(var input=response.GetResponseStream())
                            using(Stream output=destination==null?(Stream)new MemoryStream():new FileStream(destination,FileMode.CreateNew,FileAccess.Write,FileShare.None)) {
                                var buffer=new byte[32768];long total=0;int count;
                                while((count=await input.ReadAsync(buffer,0,buffer.Length,deadline.Token).ConfigureAwait(false))>0) {
                                    total+=count;if(total>limit) throw new IOException("更新文件超过大小限制。");
                                    await output.WriteAsync(buffer,0,count,deadline.Token).ConfigureAwait(false);
                                    if(progress!=null && response.ContentLength>0) progress.Report((int)Math.Min(100,total*100/response.ContentLength));
                                }
                                return destination==null?((MemoryStream)output).ToArray():null;
                            }
                        }
                    } catch(WebException) { if(token.IsCancellationRequested) throw new OperationCanceledException(token);if(deadline.IsCancellationRequested) throw new IOException("连接 GitHub 超时，请稍后重试。");throw; }
                }
            }
        }
        internal static string Hash(string path) {
            using(var hash=SHA256.Create()) using(var file=File.OpenRead(path)) return BitConverter.ToString(hash.ComputeHash(file)).Replace("-","").ToLowerInvariant();
        }
        internal static string Checksum(string text,string fileName) {
            var matches=new List<string>();
            foreach(string raw in text.Split('\n')) {
                var match=Regex.Match(raw.Trim(),@"^([a-fA-F0-9]{64})\s+\*?(.+)$");
                if(match.Success && match.Groups[2].Value==fileName) matches.Add(match.Groups[1].Value.ToLowerInvariant());
            }
            if(matches.Count!=1) throw new IOException("发布包的 SHA256 校验记录缺失或重复。");
            return matches[0];
        }
        internal static PreparedUpdate Extract(string zip,string directory,Version version) {
            string exe=Path.Combine(directory,"CodexPulse.exe"),icon=Path.Combine(directory,"pulse.ico");
            using(var archive=ZipFile.OpenRead(zip)) {
                if(archive.Entries.Count>128) throw new IOException("更新压缩包内容异常。");
                long total=0;
                foreach(var entry in archive.Entries) {
                    string name=entry.FullName.Replace('\\','/');
                    if(name.StartsWith("/") || name.Contains(":") || name.Split('/').Any(p=>p=="..")) throw new IOException("更新压缩包包含不安全路径。");
                    total+=entry.Length;if(total>128*1024*1024) throw new IOException("更新解压大小超过限制。");
                }
                foreach(string file in new[] {"CodexPulse.exe","pulse.ico"}) {
                    var entries=archive.Entries.Where(e=>e.FullName.Replace('\\','/')=="CodexPulse/"+file).ToArray();
                    if(entries.Length!=1 || entries[0].Length<=0 || entries[0].Length>(file.EndsWith(".exe")?PackageLimit:2*1024*1024)) throw new IOException("更新包缺少程序文件或包含重复文件。");
                    entries[0].ExtractToFile(Path.Combine(directory,file),false);
                }
            }
            var assembly=AssemblyName.GetAssemblyName(exe);
            if(assembly.Name!="CodexPulse" || !SameVersion(assembly.Version,version)) throw new IOException("更新程序的名称或版本与发布说明不一致。");
            return new PreparedUpdate {Directory=directory,Executable=exe,Icon=icon,Version=version.ToString(3)};
        }
        public async Task<PreparedUpdate> PrepareAsync(UpdateRelease release,IProgress<int> progress,CancellationToken token) {
            string directory=Path.Combine(Path.GetTempPath(),"CodexPulse-update-"+Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(directory);
            try {
                string checks=Encoding.UTF8.GetString(await Download(release.ChecksumUrl,65536,null,null,token));
                string expected=Checksum(checks,release.PackageName);
                if(!string.IsNullOrEmpty(release.Digest) && release.Digest!="sha256:"+expected) throw new IOException("发布元数据与 SHA256 校验文件不一致。");
                string zip=Path.Combine(directory,"package.zip");
                await Download(release.PackageUrl,PackageLimit,zip,progress,token);
                token.ThrowIfCancellationRequested();
                return await Task.Run(delegate {
                    if(new FileInfo(zip).Length!=release.Size || Hash(zip)!=expected) throw new IOException("下载文件的 SHA256 校验失败，未修改当前程序。");
                    token.ThrowIfCancellationRequested();
                    return Extract(zip,directory,release.Version);
                },token);
            } catch { try {Directory.Delete(directory,true);} catch {} throw; }
        }
    }
}
