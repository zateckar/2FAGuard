﻿using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Guard.Core.Models;
using Guard.Core.Security;
using Guard.WPF.Core.Icons;
using Guard.WPF.Core.Models;
using NSec.Cryptography;

namespace Guard.WPF.Core.Import.Importer
{
    internal class TwoFasImporter : IImporter
    {
        public string Name => "2FAS";
        public IImporter.ImportType Type => IImporter.ImportType.File;
        public string SupportedFileExtensions => "2FAS Backup (*.2fas) | *.2fas";
        private readonly JsonSerializerOptions jsonSerializerOptions =
            new() { PropertyNamingPolicy = JsonNamingPolicy.CamelCase };

        public (int total, int duplicate, int tokenID) Parse(string? path, byte[]? password)
        {
            ArgumentNullException.ThrowIfNull(path);
            byte[] data = File.ReadAllBytes(path);
            if (data.Length == 0)
            {
                throw new FormatException("The file does not contain any data.");
            }

            var json = Encoding.UTF8.GetString(data);
            var backup =
                JsonSerializer.Deserialize<TwoFasBackup>(json, jsonSerializerOptions)
                ?? throw new FormatException(
                    "Failed to parse the 2FAS backup file. JSON deserialization failed."
                );

            if (backup.SchemaVersion != 4)
            {
                throw new FormatException(
                    $"Unsupported 2FAS backup schema version: {backup.SchemaVersion}"
                );
            }

            TwoFasBackup.Service[]? services;
            if (backup.ServicesEncrypted != null)
            {
                ArgumentNullException.ThrowIfNull(password);
                services = DecryptServices(backup.ServicesEncrypted, password);
            }
            else
            {
                if (backup.Services == null || backup.Services.Length == 0)
                {
                    throw new FormatException("The 2FAS backup file does not contain any services.");
                }
                services = backup.Services;
            }

            if (services == null)
            {
                throw new FormatException(
                    "Failed to parse the 2FAS backup file because it does not contain any services."
                );
            }

            int total = 0,
                duplicate = 0,
                tokenID = 0;

            EncryptionHelper encryption = Auth.GetMainEncryptionHelper();

            foreach (var service in services)
            {
                if (service.OTP == null)
                {
                    throw new FormatException("Invalid 2FAS backup format: OTP object not found");
                }

                string issuer;

                if (service.OTP?.Issuer != null && service.OTP.Issuer.Length > 0)
                {
                    issuer = service.OTP.Issuer;
                }
                else if (service.Name != null && service.Name.Length > 0)
                {
                    issuer = service.Name;
                }
                else
                {
                    throw new Exception("Invalid 2FAS backup: No issuer or name found");
                }

                if (service.Secret == null)
                {
                    throw new ArgumentNullException("Invalid 2FAS backup: No secret found");
                }

                TotpIcon icon = IconManager.GetIcon(issuer, IconType.Any);

                if (service.OTP?.TokenType?.ToLower() != "totp")
                {
                    if (service.OTP?.TokenType?.ToLower() == "hotp")
                    {
                        throw new Exception(I18n.GetString("i.import.hotp.notsupported"));
                    }
                    throw new FormatException(
                        $"Only TOTP tokens are supported. Backup contains {service.OTP?.TokenType} token."
                    );
                }

                string normalizedSecret = OTPUriParser.NormalizeSecret(service.Secret);

                if (!OTPUriParser.IsValidSecret(normalizedSecret))
                {
                    throw new FormatException($"{I18n.GetString("td.invalidsecret")} ({issuer})");
                }

                DBTOTPToken dbToken =
                    new()
                    {
                        Id = TokenManager.GetNextId(),
                        Issuer = issuer,
                        EncryptedSecret = encryption.EncryptStringToBytes(normalizedSecret),
                        CreationTime = DateTime.Now
                    };

                if (service.OTP.Account != null)
                {
                    dbToken.EncryptedUsername = encryption.EncryptStringToBytes(
                        service.OTP.Account
                    );
                }

                if (icon != null && icon.Type != IconType.Default)
                {
                    dbToken.Icon = icon.Name;
                    dbToken.IconType = icon.Type;
                }

                if (service.OTP.Algorithm != null)
                {
                    dbToken.Algorithm = service.OTP.Algorithm.ToUpper() switch
                    {
                        "SHA1" => TOTPAlgorithm.SHA1,
                        "SHA256" => TOTPAlgorithm.SHA256,
                        "SHA512" => TOTPAlgorithm.SHA512,
                        _
                            => throw new FormatException(
                                $"Invalid 2FAS backup: Unsupported algorithm {service.OTP.Algorithm}"
                            ),
                    };
                }

                if (service.OTP.Digits != null && service.OTP.Digits > 0)
                {
                    dbToken.Digits = service.OTP.Digits;
                }

                if (service.OTP.Period != null && service.OTP.Period > 0)
                {
                    dbToken.Period = service.OTP.Period;
                }

                total += 1;
                if (!TokenManager.AddToken(dbToken))
                {
                    duplicate += 1;
                }
                else
                {
                    tokenID = dbToken.Id;
                }
            }

            return (total, duplicate, tokenID);
        }

        public bool RequiresPassword(string? path)
        {
            ArgumentNullException.ThrowIfNull(path);

            byte[] data = File.ReadAllBytes(path);
            if (data.Length == 0)
            {
                throw new FormatException("The 2FAS backup file does not contain any data.");
            }

            var json = Encoding.UTF8.GetString(data);
            var backup =
                JsonSerializer.Deserialize<TwoFasBackup>(json, jsonSerializerOptions)
                ?? throw new FormatException(
                    "Failed to parse the 2FAS backup file. JSON deserialization failed."
                );

            if (backup.ServicesEncrypted != null)
            {
                return true;
            }

            return false;
        }

        internal TwoFasBackup.Service[]? DecryptServices(string encrypted, byte[] pass)
        {
            string[] parts = encrypted.Split(":");
            if (parts.Length != 3)
            {
                throw new ArgumentException("Invalid encypted 2FAS backup: Invalid parts count");
            }

            ReadOnlySpan<byte> encryptedData = Convert.FromBase64String(parts[0]);
            byte[] salt = Convert.FromBase64String(parts[1]);
            ReadOnlySpan<byte> iv = Convert.FromBase64String(parts[2]);

            ReadOnlySpan<byte> keyBytes = new Rfc2898DeriveBytes(
                pass,
                salt,
                100000,
                HashAlgorithmName.SHA256
            ).GetBytes(32);

            if (!Aes256Gcm.IsSupported)
            {
                throw new FormatException(
                    "AES256-GCM is not supported on this platform. The reason may be that your CPU does not support hardware-accelerated AES256-GCM encryption."
                );
            }

            Aes256Gcm aes = new();
            Key key = Key.Import(aes, keyBytes, KeyBlobFormat.RawSymmetricKey);

            byte[]? decryptedData =
                aes.Decrypt(key, iv, null, encryptedData)
                ?? throw new FormatException(I18n.GetString("import.password.invalid"));
            return JsonSerializer.Deserialize<TwoFasBackup.Service[]>(
                Encoding.UTF8.GetString(decryptedData),
                jsonSerializerOptions
            );
        }
    }
}
