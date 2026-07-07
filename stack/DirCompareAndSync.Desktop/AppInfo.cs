using System.Reflection;
using System.Runtime.InteropServices;
using DirCompareAndSync.Desktop.Deploy;

namespace DirCompareAndSync.Desktop;

public static class AppInfo
{
    public const string ProductName = "DirCompareAndSync";
    public const string VersionDisplay = "2.24";
    public const string Tagline =
        "Comparação e sincronização de pastas com pré-visualização antes de sincronizar.";
    public const string FreeUsageNotice =
        "O DirCompareAndSync é gratuito para utilização pessoal, profissional e em ambiente empresarial " +
        "(empresas, organizações, escolas, serviços públicos e outras entidades), sem custo de licenciamento " +
        "por posto de trabalho, por servidor ou por volume de dados sincronizados. " +
        "Pode instalar e usar em todos os computadores da sua organização sem taxas de uso do software.";
    public const string Copyright = "© 2026 Élio Mota. Todos os direitos reservados.";
    public const string ContactEmail = "elio.mota@gmail.com";
    public const string Credits =
        "Interface Avalonia";

    public const string UsageDisclaimer =
        "A utilização deste software é da exclusiva responsabilidade do utilizador. " +
        "Não garantimos a ausência de erros nem a integridade dos dados; " +
        "recomenda-se efectuar cópias de segurança antes de sincronizar ou apagar ficheiros.";

    public const string AttributionNotice =
        "Quem utilizar este software — incluindo qualquer funcionalidade da interface gráfica, " +
        "da linha de comandos, dos agendamentos, do envio de e-mail ou da sincronização — " +
        "deve manter referência visível ao projecto DirCompareAndSync e ao respectivo autor " +
        "(Elio Mota), salvo acordo escrito em contrário.";

    public const string AuthorName = "Élio Mota";

    public static string WindowTitle => $"{ProductName} v{VersionDisplay} :: © {AuthorName}";

    public static string AssemblyVersion =>
        FormatVersion(Assembly.GetExecutingAssembly().GetName().Version) ?? VersionDisplay;

    private static string? FormatVersion(System.Version? version)
    {
        if (version is null)
        {
            return null;
        }

        var parts = new List<string>
        {
            version.Major.ToString(),
            version.Minor.ToString()
        };

        if (version.Build >= 0)
        {
            parts.Add(version.Build.ToString());
        }

        if (version.Revision >= 0)
        {
            parts.Add(version.Revision.ToString());
        }

        return string.Join('.', parts);
    }

    public static string VersionLine => $"Versão {VersionDisplay} (build {AssemblyVersion})";

    public const string MinimumRequirements =
        "• Windows 10 ou superior (64 bits) — instalador oficial\n" +
        "• Resolução mínima: 1050 × 680 px\n" +
        "• Espaço em disco: ~150 MB (instalação completa)\n" +
        "• Acesso de leitura/escritura às pastas a comparar ou sincronizar\n" +
        "• Ligação de rede, se usar partilhas UNC, FTP/FTPS ou envio de e-mail\n" +
        "• Linux e macOS: suportados com .NET 10 (sem instalador Velopack)";

    public static string RuntimeLine =>
        $"{RuntimeInformation.FrameworkDescription} · {RuntimeInformation.ProcessArchitecture} · {RuntimeInformation.OSDescription}";

    public static string? RepositoryUrl => AppDeployInfo.ResolveGitHubRepoUrl();
}
