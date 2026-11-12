using System;
using System.IO;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text.Json;
using NekoBridge.Core;

namespace NekoBridge.Windows;

internal sealed class LocalState
{
    private readonly object _gate=new();
    internal string DirectoryPath {get;}
    internal AppSettings Settings {get;}
    internal X509Certificate2 Identity {get;}
    internal LocalState(string? directory=null)
    {
        DirectoryPath=directory??Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),"NekoBridge");
        Directory.CreateDirectory(DirectoryPath);
        string file=Path.Combine(DirectoryPath,"settings.json");
        Settings=File.Exists(file)?JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(file))??throw new InvalidDataException("配置文件为空"):new AppSettings();
        if(string.IsNullOrWhiteSpace(Settings.DeviceName))Settings.DeviceName=Environment.MachineName;
        if(string.IsNullOrWhiteSpace(Settings.ReceiveFolder))Settings.ReceiveFolder=Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),"Downloads","NekoBridge");
        if(Settings.Id==Guid.Empty)throw new InvalidDataException("设备身份无效，未自动重置配置。");
        Identity=LoadIdentity();
        Save();
    }
    internal void Save()
    {
        lock(_gate)
        {
            string file=Path.Combine(DirectoryPath,"settings.json"),temp=file+".tmp";
            File.WriteAllText(temp,JsonSerializer.Serialize(Settings,new JsonSerializerOptions{WriteIndented=true}));
            File.Move(temp,file,true);
        }
    }
    private X509Certificate2 LoadIdentity()
    {
        string marker=Path.Combine(DirectoryPath,"identity-thumbprint.txt");
        using var store=new X509Store(StoreName.My,StoreLocation.CurrentUser);
        store.Open(OpenFlags.ReadWrite);
        if(File.Exists(marker))
        {
            string thumb=File.ReadAllText(marker).Trim();
            var matches=store.Certificates.Find(X509FindType.FindByThumbprint,thumb,false);
            if(matches.Count==0||!matches[0].HasPrivateKey)throw new CryptographicException("设备证书不存在或私钥不可用，未自动替换受信任身份。");
            return matches[0];
        }
        using var rsa=RSA.Create(3072);
        var request=new CertificateRequest($"CN=NekoBridge-{Settings.Id:N}",rsa,HashAlgorithmName.SHA256,RSASignaturePadding.Pkcs1);
        request.CertificateExtensions.Add(new X509BasicConstraintsExtension(false,false,0,true));
        request.CertificateExtensions.Add(new X509KeyUsageExtension(X509KeyUsageFlags.DigitalSignature|X509KeyUsageFlags.KeyEncipherment,true));
        var usages=new OidCollection{new Oid("1.3.6.1.5.5.7.3.1"),new Oid("1.3.6.1.5.5.7.3.2")};
        request.CertificateExtensions.Add(new X509EnhancedKeyUsageExtension(usages,false));
        using var generated=request.CreateSelfSigned(DateTimeOffset.UtcNow.AddDays(-1),DateTimeOffset.UtcNow.AddYears(5));
        byte[] bytes=generated.Export(X509ContentType.Pfx);
        try
        {
            var certificate=X509CertificateLoader.LoadPkcs12(bytes,null,X509KeyStorageFlags.PersistKeySet|X509KeyStorageFlags.UserKeySet);
            certificate.FriendlyName="NekoBridge device identity";
            store.Add(certificate);
            File.WriteAllText(marker,certificate.Thumbprint);
            return certificate;
        }
        finally{CryptographicOperations.ZeroMemory(bytes);}
    }
}
