using PdfSharp.Pdf;
using PdfSharp.Pdf.IO;
using Petal.Engines.Documents.Util;

namespace Petal.Engines.Documents.Pdf;

/// <param name="OpenPassword">Needed to open the PDF at all.</param>
/// <param name="AllowPrinting">Readers may print.</param>
/// <param name="AllowCopying">Readers may copy text and images out.</param>
/// <param name="AllowEditing">Readers may change pages, forms and comments.</param>
public sealed record PdfProtectOptions(string OpenPassword, bool AllowPrinting = true, bool AllowCopying = true, bool AllowEditing = false);

/// <summary>Password protection with AES-256, and removing it again.</summary>
public static class PdfSecurityTools
{
    /// <summary>True when the PDF asks for a password before it opens.</summary>
    public static bool NeedsPassword(string path)
    {
        try
        {
            using var _ = PdfSharpIO.Open(path, PdfDocumentOpenMode.InformationOnly);
            return false;
        }
        catch (PdfOpenException ex) when (ex.NeedsPassword)
        {
            return true;
        }
    }

    /// <summary>True when <paramref name="password"/> opens the PDF.</summary>
    public static bool CanOpen(string path, string password)
    {
        try
        {
            using var _ = PdfSharpIO.Open(path, PdfDocumentOpenMode.InformationOnly, password);
            return true;
        }
        catch (PdfOpenException)
        {
            return false;
        }
    }

    /// <summary>
    /// False when AES-256 PDF encryption can't use the password. Its password rules (SASLprep)
    /// require a password with Arabic or Hebrew letters to start and end with one and not to mix
    /// in Latin letters; digits and symbols in the middle are fine. Checked with the same code
    /// that encrypts, on a throwaway document.
    /// </summary>
    public static bool IsUsablePassword(string password)
    {
        if (string.IsNullOrEmpty(password))
            return false;
        try
        {
            using var probe = new PdfDocument();
            probe.AddPage();
            probe.SecurityHandler.SetEncryptionToV5(true);
            probe.SecuritySettings.UserPassword = password;
            probe.SecuritySettings.OwnerPassword = "probe-owner";
            probe.Save(Stream.Null);
            return true;
        }
        catch (ArgumentException)
        {
            return false;
        }
    }

    /// <summary>
    /// Saves a copy that asks for <see cref="PdfProtectOptions.OpenPassword"/>. A separate, random
    /// owner password holds the permissions, so they can't be lifted with the open password.
    /// </summary>
    public static Task ProtectAsync(string input, string output, PdfProtectOptions options,
        IProgress<double>? progress = null, CancellationToken cancellationToken = default) =>
        Task.Run(() =>
        {
            SafeOutput.EnsureNotInput(input, output);
            if (string.IsNullOrEmpty(options.OpenPassword))
                throw new ArgumentException("Choose a password.", nameof(options));
            if (!IsUsablePassword(options.OpenPassword))
                throw new ArgumentException("A password with Arabic letters must start and end with one, and can't also contain Latin letters.", nameof(options));
            using var document = PdfSharpIO.Open(input, PdfDocumentOpenMode.Modify);
            progress?.Report(0.4);
            cancellationToken.ThrowIfCancellationRequested();
            document.SecurityHandler.SetEncryptionToV5(true);
            var settings = document.SecuritySettings;
            settings.UserPassword = options.OpenPassword;
            settings.OwnerPassword = Convert.ToHexString(System.Security.Cryptography.RandomNumberGenerator.GetBytes(16));
            settings.PermitPrint = settings.PermitFullQualityPrint = options.AllowPrinting;
            settings.PermitExtractContent = options.AllowCopying;
            settings.PermitModifyDocument = settings.PermitAnnotations = settings.PermitFormsFill = settings.PermitAssembleDocument = options.AllowEditing;
            SafeOutput.Write(output, temp => PdfSharpIO.Save(document, temp));
            progress?.Report(1);
        }, cancellationToken);

    /// <summary>
    /// Saves an unprotected copy, given the password that opens it. The pages, bookmarks and
    /// document details are copied into a new file (editing in place would need the owner password).
    /// </summary>
    public static async Task UnlockAsync(string input, string output, string password, IProgress<double>? progress = null,
        CancellationToken cancellationToken = default)
    {
        int count = await Task.Run(() =>
        {
            using var document = PdfSharpIO.Open(input, PdfDocumentOpenMode.Import, password);
            return document.PageCount;
        }, cancellationToken).ConfigureAwait(false);
        var pages = Enumerable.Range(0, count).Select(i => new PdfPageOperation(i)).ToList();
        await PdfPageTools.OrganizeAsync(input, output, pages, new PdfOrganizeOptions(Password: password), progress, cancellationToken).ConfigureAwait(false);
    }
}
