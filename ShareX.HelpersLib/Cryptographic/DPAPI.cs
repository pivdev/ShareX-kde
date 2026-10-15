#region License Information (GPL v3)

/*
    ShareX - A program that allows you to take screenshots and share any file type
    Copyright (c) 2007-2026 ShareX Team

    This program is free software; you can redistribute it and/or
    modify it under the terms of the GNU General Public License
    as published by the Free Software Foundation; either version 2
    of the License, or (at your option) any later version.

    This program is distributed in the hope that it will be useful,
    but WITHOUT ANY WARRANTY; without even the implied warranty of
    MERCHANTABILITY or FITNESS FOR A PARTICULAR PURPOSE.  See the
    GNU General Public License for more details.

    You should have received a copy of the GNU General Public License
    along with this program; if not, write to the Free Software
    Foundation, Inc., 51 Franklin Street, Fifth Floor, Boston, MA  02110-1301, USA.

    Optionally you can also view the license at <http://www.gnu.org/licenses/>.
*/

#endregion License Information (GPL v3)

using System;
using System.IO;
using System.Security.Cryptography;
using System.Text;

namespace ShareX.HelpersLib
{
#if LINUX
    public enum DataProtectionScope
    {
        CurrentUser,
        LocalMachine
    }

    // Linux stand-in for Windows DPAPI: AES-GCM with a random per-user key kept in a 0600 file.
    internal static class ProtectedData
    {
        private const int NonceSize = 12;
        private const int TagSize = 16;

        private static readonly Lazy<byte[]> key = new Lazy<byte[]>(LoadOrCreateKey);

        private static string KeyFilePath
        {
            get
            {
                string dataHome = Environment.GetEnvironmentVariable("XDG_DATA_HOME");
                if (string.IsNullOrEmpty(dataHome))
                {
                    dataHome = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".local", "share");
                }
                return Path.Combine(dataHome, "ShareX", "dpapi.key");
            }
        }

        private static byte[] LoadOrCreateKey()
        {
            string path = KeyFilePath;
            if (File.Exists(path))
            {
                byte[] existing = File.ReadAllBytes(path);
                if (existing.Length == 32) return existing;
            }

            Directory.CreateDirectory(Path.GetDirectoryName(path));
            byte[] newKey = RandomNumberGenerator.GetBytes(32);
            using (FileStream fs = new FileStream(path, new FileStreamOptions
            {
                Mode = FileMode.Create,
                Access = FileAccess.Write,
                UnixCreateMode = UnixFileMode.UserRead | UnixFileMode.UserWrite
            }))
            {
                fs.Write(newKey);
            }
            return newKey;
        }

        public static byte[] Protect(byte[] userData, byte[] optionalEntropy, DataProtectionScope scope)
        {
            byte[] nonce = RandomNumberGenerator.GetBytes(NonceSize);
            byte[] cipher = new byte[userData.Length];
            byte[] tag = new byte[TagSize];
            using (AesGcm aes = new AesGcm(key.Value, TagSize))
            {
                aes.Encrypt(nonce, userData, cipher, tag, optionalEntropy);
            }
            byte[] result = new byte[NonceSize + TagSize + cipher.Length];
            nonce.CopyTo(result, 0);
            tag.CopyTo(result, NonceSize);
            cipher.CopyTo(result, NonceSize + TagSize);
            return result;
        }

        public static byte[] Unprotect(byte[] encryptedData, byte[] optionalEntropy, DataProtectionScope scope)
        {
            ReadOnlySpan<byte> span = encryptedData;
            byte[] plain = new byte[encryptedData.Length - NonceSize - TagSize];
            using (AesGcm aes = new AesGcm(key.Value, TagSize))
            {
                aes.Decrypt(span[..NonceSize], span[(NonceSize + TagSize)..], span.Slice(NonceSize, TagSize), plain, optionalEntropy);
            }
            return plain;
        }
    }
#endif

    // https://en.wikipedia.org/wiki/Data_Protection_API
    public static class DPAPI
    {
        public static string Encrypt(string stringToEncrypt, string optionalEntropy = null, DataProtectionScope scope = DataProtectionScope.CurrentUser)
        {
            byte[] data = Encoding.UTF8.GetBytes(stringToEncrypt);
            return Encrypt(data, optionalEntropy, scope);
        }

        public static string Encrypt(byte[] data, string optionalEntropy = null, DataProtectionScope scope = DataProtectionScope.CurrentUser)
        {
            byte[] entropyData = null;
            if (optionalEntropy != null)
            {
                entropyData = Encoding.UTF8.GetBytes(optionalEntropy);
            }
            return Encrypt(data, entropyData, scope);
        }

        public static string Encrypt(byte[] data, byte[] optionalEntropy = null, DataProtectionScope scope = DataProtectionScope.CurrentUser)
        {
            byte[] encryptedData = ProtectedData.Protect(data, optionalEntropy, scope);
            return Convert.ToBase64String(encryptedData);
        }

        public static string Decrypt(string encryptedString, string optionalEntropy = null, DataProtectionScope scope = DataProtectionScope.CurrentUser)
        {
            byte[] encryptedData = Convert.FromBase64String(encryptedString);
            return Decrypt(encryptedData, optionalEntropy, scope);
        }

        public static string Decrypt(byte[] encryptedData, string optionalEntropy = null, DataProtectionScope scope = DataProtectionScope.CurrentUser)
        {
            byte[] entropyData = null;
            if (optionalEntropy != null)
            {
                entropyData = Encoding.UTF8.GetBytes(optionalEntropy);
            }
            return Decrypt(encryptedData, entropyData, scope);
        }

        public static string Decrypt(byte[] encryptedData, byte[] optionalEntropy = null, DataProtectionScope scope = DataProtectionScope.CurrentUser)
        {
            byte[] decryptedData = ProtectedData.Unprotect(encryptedData, optionalEntropy, scope);
            return Encoding.UTF8.GetString(decryptedData);
        }
    }
}