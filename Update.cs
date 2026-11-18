// アップデートの確認と差し替え
//   GitHub Releases の最新版を確認し、新しければ猫が知らせて右クリックメニューに「アップデート」を出す（勝手には更新しない）
//   差し替えるのは、ダウンロードした exe の署名が壊れておらず、今の exe と同じ証明書で署名されている時だけ（改ざん・なりすまし対策）
//   動いている exe は上書きできないが名前は変えられるので、自分を .old に退避 → 新しい exe を置く → 再起動、の順で差し替える
using System;
using System.Diagnostics;
using System.IO;
using System.Net;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Windows.Forms;

[assembly: AssemblyVersion(Updater.Version)]
[assembly: AssemblyFileVersion(Updater.Version)]

static class Updater
{
    public const string Version = "1.0.0";   // リリースのたびに上げる。リリースのタグは v + この番号（例: v1.0.0）
    const string Repo = "raisack8/taskbar-cat";
    const string AssetName = "TaskbarCat.exe";   // リリースに添付する exe の名前
    public const string ReleasesPage = "https://github.com/" + Repo + "/releases/latest";

    public class Release { public Version Ver; public string ExeUrl; }

    public static Version Current { get { return Norm(Version); } }

    // "v1.2" や "1.2.0.0" を 3 桁 (1.2.0) にそろえる。読めなければ null
    static Version Norm(string s)
    {
        Version v;
        if (s == null || !System.Version.TryParse(s.Trim().TrimStart('v', 'V'), out v)) return null;
        return new Version(v.Major, v.Minor, Math.Max(0, v.Build));
    }

    static HttpWebResponse Get(string url, string accept)
    {
        ServicePointManager.SecurityProtocol |= (SecurityProtocolType)3072;   // TLS 1.2（.NET 4 の既定では無効なことがある）
        var req = (HttpWebRequest)WebRequest.Create(url);
        req.UserAgent = "TaskbarCat/" + Version;   // GitHub API は User-Agent 必須
        req.Accept = accept;
        req.Timeout = 15000;
        req.ReadWriteTimeout = 30000;
        return (HttpWebResponse)req.GetResponse();
    }

    // 今より新しいリリースがあれば返す。無ければ null。通信できない時は例外
    public static Release FindNewer()
    {
        string json;
        try
        {
            using (var res = Get("https://api.github.com/repos/" + Repo + "/releases/latest", "application/vnd.github+json"))
            using (var r = new StreamReader(res.GetResponseStream(), Encoding.UTF8)) json = r.ReadToEnd();
        }
        catch (WebException ex)
        {
            var res = ex.Response as HttpWebResponse;
            if (res != null && res.StatusCode == HttpStatusCode.NotFound) return null;   // リリースがまだ無い
            throw;
        }
        var tag = Regex.Match(json, "\"tag_name\"\\s*:\\s*\"([^\"]+)\"");
        var v = tag.Success ? Norm(tag.Groups[1].Value) : null;
        if (v == null || v <= Current) return null;
        // ダウンロード元はこのリポジトリのリリースに限る
        var url = Regex.Match(json, "\"browser_download_url\"\\s*:\\s*\"(https://github\\.com/" + Regex.Escape(Repo) +
            "/releases/download/[^\"/]+/" + Regex.Escape(AssetName) + ")\"");
        if (!url.Success) return null;   // exe が添付されていないリリース
        return new Release { Ver = v, ExeUrl = url.Groups[1].Value };
    }

    // 新しい exe を落として確かめ、今の exe と差し替える。このあと再起動すれば新しい版になる
    // 戻り値: 差し替え自体は成功したが気になることがあった時のメモ（ログ用）。無ければ null
    public static string Apply(Release r)
    {
        string exe = Application.ExecutablePath, tmp = exe + ".new", old = exe + ".old";
        try
        {
            using (var res = Get(r.ExeUrl, "application/octet-stream"))
            {
                if (res.ContentLength > 50L * 1024 * 1024) throw new Exception("ダウンロードするファイルが大きすぎます。");
                using (var src = res.GetResponseStream())
                using (var dst = File.Create(tmp)) src.CopyTo(dst);
            }
            Verify(exe, tmp, r.Ver);
            if (File.Exists(old)) File.Delete(old);
            File.Move(exe, old);
            try { File.Move(tmp, exe); }
            catch { File.Move(old, exe); throw; }
        }
        catch (UnauthorizedAccessException)
        {
            throw new Exception("exe のあるフォルダに書き込めません（" + Path.GetDirectoryName(exe) + "）。");
        }
        finally { try { File.Delete(tmp); } catch { } }

        // shell:startup に exe そのものが置いてあれば、それも差し替える（動いていないので上書きできる）
        string su = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Startup), AssetName);
        if (File.Exists(su) && !string.Equals(Path.GetFullPath(su), Path.GetFullPath(exe), StringComparison.OrdinalIgnoreCase))
        {
            try { File.Copy(exe, su, true); return "スタートアップの " + su + " も差し替えました"; }
            catch (Exception ex) { return "スタートアップの " + su + " は差し替えられませんでした : " + ex.Message; }
        }
        return null;
    }

    // ダウンロードした exe が「壊れていない署名付き」「今の exe と同じ証明書」「リリースの番号どおりの版」かを確かめる
    internal static void Verify(string current, string downloaded, Version expect)
    {
        string mine = Thumbprint(current);
        if (mine == null) throw new Exception("今の TaskbarCat.exe に署名が無いため、ダウンロードした exe が本物か確かめられません。");
        int rc = WinVerify(downloaded);
        // 自己署名なのでルート証明書が信頼されていない (CERT_E_UNTRUSTEDROOT) のは想定どおり。中身の改ざん (TRUST_E_BAD_DIGEST) などは不可
        if (rc != 0 && rc != unchecked((int)0x800B0109))
            throw new Exception("ダウンロードした exe の署名が正しくありません (0x" + rc.ToString("X8") + ")。");
        if (Thumbprint(downloaded) != mine)
            throw new Exception("ダウンロードした exe は、今の exe と別の証明書で署名されています。\n（自分でビルドした exe は自動では更新できません。リリースページから手動で入れ替えてください）");
        var fv = Norm(FileVersionInfo.GetVersionInfo(downloaded).FileVersion);
        if (fv == null || fv != expect)
            throw new Exception("ダウンロードした exe の版 (" + fv + ") がリリースの番号 (" + expect + ") と違います。");
    }

    static string Thumbprint(string path)
    {
        try { return new X509Certificate2(X509Certificate.CreateFromSignedFile(path)).Thumbprint; }
        catch { return null; }
    }

    // 前回のアップデートで退避した .old を消す（古い猫が終わりきるまで消せないので少し待ちながら）
    public static void CleanupOld()
    {
        string old = Application.ExecutablePath + ".old";
        if (!File.Exists(old)) return;
        ThreadPool.QueueUserWorkItem(_ =>
        {
            for (int i = 0; i < 10 && File.Exists(old); i++)
                try { File.Delete(old); } catch { Thread.Sleep(1000); }
        });
    }

    // ---- Authenticode の検証 (WinVerifyTrust) ----
    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    struct WINTRUST_FILE_INFO { public int cbStruct; public string pcwszFilePath; public IntPtr hFile, pgKnownSubject; }
    [StructLayout(LayoutKind.Sequential)]
    struct WINTRUST_DATA
    {
        public int cbStruct; public IntPtr pPolicyCallbackData, pSIPClientData;
        public int dwUIChoice, fdwRevocationChecks, dwUnionChoice; public IntPtr pFile;
        public int dwStateAction; public IntPtr hWVTStateData, pwszURLReference;
        public int dwProvFlags, dwUIContext; public IntPtr pSignatureSettings;
    }
    [DllImport("wintrust.dll", CharSet = CharSet.Unicode)] static extern int WinVerifyTrust(IntPtr hwnd, ref Guid action, ref WINTRUST_DATA data);

    static int WinVerify(string path)
    {
        var fi = new WINTRUST_FILE_INFO { cbStruct = Marshal.SizeOf(typeof(WINTRUST_FILE_INFO)), pcwszFilePath = path };
        IntPtr pfi = Marshal.AllocHGlobal(fi.cbStruct);
        try
        {
            Marshal.StructureToPtr(fi, pfi, false);
            var d = new WINTRUST_DATA
            {
                cbStruct = Marshal.SizeOf(typeof(WINTRUST_DATA)),
                dwUIChoice = 2,              // WTD_UI_NONE
                dwUnionChoice = 1,           // WTD_CHOICE_FILE
                pFile = pfi,
                dwProvFlags = 0x10 | 0x1000  // WTD_REVOCATION_CHECK_NONE | WTD_CACHE_ONLY_URL_RETRIEVAL（失効確認の通信はしない）
            };
            var action = new Guid("00AAC56B-CD44-11d0-8CC2-00C04FC295EE");   // WINTRUST_ACTION_GENERIC_VERIFY_V2
            return WinVerifyTrust(new IntPtr(-1), ref action, ref d);
        }
        finally
        {
            Marshal.DestroyStructure(pfi, typeof(WINTRUST_FILE_INFO));
            Marshal.FreeHGlobal(pfi);
        }
    }
}
