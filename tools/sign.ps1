# TaskbarCat.exe に自己署名する:  build.bat から呼ばれる
#   証明書「CN=TaskbarCat Dev」が現在のユーザーの証明書ストアに無ければ作る（2回目以降は使い回す）
#   自己署名なので SmartScreen の警告は消えない。改ざん検知と「誰がビルドしたか」の目印用
#   信頼されたルートへの登録はしない（セキュリティ設定の変更になるため）
$ErrorActionPreference = 'Stop'
$exe = Join-Path (Split-Path $PSScriptRoot -Parent) 'TaskbarCat.exe'
$subject = 'CN=TaskbarCat Dev'

$cert = Get-ChildItem Cert:\CurrentUser\My -CodeSigningCert |
    Where-Object { $_.Subject -eq $subject -and $_.NotAfter -gt (Get-Date) } |
    Sort-Object NotAfter -Descending | Select-Object -First 1
if (-not $cert) {
    $cert = New-SelfSignedCertificate -Type CodeSigningCert -Subject $subject `
        -CertStoreLocation Cert:\CurrentUser\My -NotAfter (Get-Date).AddYears(5)
    Write-Host "署名用の証明書を作りました: $subject （$($cert.Thumbprint)）"
}

# タイムスタンプは付けない（外部サーバーへの通信が要るため。自己署名では効果も薄い）
$sig = Set-AuthenticodeSignature $exe -Certificate $cert -HashAlgorithm SHA256
if (-not $sig.SignerCertificate) { throw "署名できませんでした: $($sig.StatusMessage)" }
# 自己署名はルートが信頼されていないので Status は UnknownError になる。署名自体は付いている
Write-Host "署名しました: $subject （有効期限 $($cert.NotAfter.ToString('yyyy-MM-dd'))）"
