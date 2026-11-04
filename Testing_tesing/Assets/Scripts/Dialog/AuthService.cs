using System;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Xml.Linq;
using UnityEngine;

namespace Dialog
{
    // Local-only profiles. Stores username (a pseudonym), salt and PBKDF2 hash, plus an
    // anonymous PlayerId that history files should use instead of the name.
    public static class AuthService
    {
        const int Iterations = 100_000;
        static string FilePath => Path.Combine(Application.persistentDataPath, "users.xml");

        public static string CurrentUserId { get; private set; }
        public static string CurrentUserName { get; private set; }

        public static bool TryRegister(string name, string password, out string error)
        {
            error = null;
            name = (name ?? "").Trim();
            if (name.Length < 3) { error = "Username must be at least 3 characters."; return false; }
            if (password == null || password.Length < 6) { error = "Password must be at least 6 characters."; return false; }

            XDocument doc = Load();
            if (doc.Root.Elements("User").Any(u => string.Equals((string)u.Attribute("name"), name, StringComparison.OrdinalIgnoreCase)))
            {
                error = "This username is already taken.";
                return false;
            }

            byte[] salt = new byte[16];
            using (var rng = RandomNumberGenerator.Create())
                rng.GetBytes(salt);
            string id = Guid.NewGuid().ToString("N");
            doc.Root.Add(new XElement("User",
                new XAttribute("id", id),
                new XAttribute("name", name),
                new XAttribute("salt", Convert.ToBase64String(salt)),
                new XAttribute("hash", Convert.ToBase64String(Hash(password, salt)))));
            doc.Save(FilePath);

            CurrentUserId = id;
            CurrentUserName = name;
            return true;
        }

        public static bool TryLogin(string name, string password, out string error)
        {
            error = "Invalid username or password."; // same message for both cases on purpose
            name = (name ?? "").Trim();
            XElement user = Load().Root.Elements("User")
                .FirstOrDefault(u => string.Equals((string)u.Attribute("name"), name, StringComparison.OrdinalIgnoreCase));
            if (user == null || string.IsNullOrEmpty(password))
                return false;

            byte[] salt = Convert.FromBase64String((string)user.Attribute("salt"));
            byte[] expected = Convert.FromBase64String((string)user.Attribute("hash"));
            if (!CryptographicOperations.FixedTimeEquals(Hash(password, salt), expected))
                return false;

            CurrentUserId = (string)user.Attribute("id");
            CurrentUserName = (string)user.Attribute("name");
            error = null;
            return true;
        }

        public static void Logout() { CurrentUserId = null; CurrentUserName = null; }

        static byte[] Hash(string password, byte[] salt)
        {
            using var kdf = new Rfc2898DeriveBytes(password, salt, Iterations, HashAlgorithmName.SHA256);
            return kdf.GetBytes(32);
        }

        static XDocument Load()
        {
            try { if (File.Exists(FilePath)) return XDocument.Load(FilePath); }
            catch (Exception e) { Debug.LogWarning("[Auth] users.xml unreadable, starting fresh: " + e.Message); }
            return new XDocument(new XElement("Users"));
        }
    }
}