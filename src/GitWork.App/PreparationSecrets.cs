using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;

namespace GitWork.App;

public static partial class PreparationSecrets
{
    private const string Prefix = "dpapi:";

    public static string Protect(string value) => PreparationSecrets.Prefix
        + Convert.ToBase64String(PreparationSecrets.Transform(Encoding.UTF8.GetBytes(value), true));

    public static string Unprotect(string value)
    {
        if (!value.StartsWith(PreparationSecrets.Prefix, StringComparison.Ordinal))
        {
            throw new CryptographicException("Секрет не защищён. Задайте значение заново в настройках профиля.");
        }

        var bytes = PreparationSecrets.Transform(Convert.FromBase64String(value[PreparationSecrets.Prefix.Length..]), false);
        try
        {
            return Encoding.UTF8.GetString(bytes);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(bytes);
        }
    }

    private static byte[] Transform(byte[] bytes, bool protect)
    {
        var input = new DataBlob { Length = bytes.Length, Data = Marshal.AllocHGlobal(Math.Max(1, bytes.Length)) };
        var output = new DataBlob();
        try
        {
            Marshal.Copy(bytes, 0, input.Data, bytes.Length);
            var succeeded = protect
                ? PreparationSecrets.CryptProtectData(ref input, 0, 0, 0, 0, 1, out output)
                : PreparationSecrets.CryptUnprotectData(ref input, 0, 0, 0, 0, 1, out output);
            if (!succeeded)
            {
                throw new CryptographicException("Не удалось открыть или сохранить секрет текущего пользователя Windows.",
                    new Win32Exception(Marshal.GetLastPInvokeError()));
            }

            var result = new byte[output.Length];
            Marshal.Copy(output.Data, result, 0, result.Length);

            return result;
        }
        finally
        {
            CryptographicOperations.ZeroMemory(bytes);
            PreparationSecrets.Clear(input);
            Marshal.FreeHGlobal(input.Data);
            if (output.Data != 0)
            {
                PreparationSecrets.Clear(output);
                PreparationSecrets.LocalFree(output.Data);
            }
        }
    }

    private static unsafe void Clear(DataBlob blob) => CryptographicOperations.ZeroMemory(new Span<byte>((void*)blob.Data, blob.Length));

    [StructLayout(LayoutKind.Sequential)]
    private struct DataBlob
    {
        public int Length;
        public nint Data;
    }

    [LibraryImport("crypt32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool CryptProtectData(ref DataBlob input, nint description, nint entropy, nint reserved, nint prompt, int flags, out DataBlob output);

    [LibraryImport("crypt32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool CryptUnprotectData(ref DataBlob input, nint description, nint entropy, nint reserved, nint prompt, int flags, out DataBlob output);

    [LibraryImport("kernel32.dll")]
    private static partial nint LocalFree(nint memory);
}
