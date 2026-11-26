using System.Security.AccessControl;
using System.Security.Principal;
using IabsAgenteSenior.Sistema;

namespace IabsAgenteSenior.Testes;

/// <summary>
/// A pasta de dados fica só para SYSTEM, administradores, o serviço e quem o
/// instalador liberou (26/11/2026: quem instalou abre a janela sem o controle
/// de conta de usuário). O grupo Usuários nunca entra: a pasta tem a senha do
/// banco, e a cifra é de máquina.
/// </summary>
public sealed class PastaDadosTestes : IDisposable
{
    private readonly string _pasta = Path.Combine(Path.GetTempPath(), $"iabs-pasta-teste-{Guid.NewGuid():N}");

    public void Dispose()
    {
        if (Directory.Exists(_pasta)) Directory.Delete(_pasta, recursive: true);
    }

    [Fact]
    public void Protege_e_mantem_quem_foi_liberado()
    {
        var eu = WindowsIdentity.GetCurrent().User!;
        PastaDados.Proteger(_pasta, [eu]);

        var acl = new DirectoryInfo(_pasta).GetAccessControl();
        Assert.True(acl.AreAccessRulesProtected);
        var regras = acl.GetAccessRules(true, true, typeof(SecurityIdentifier)).Cast<FileSystemAccessRule>().ToList();
        Assert.Contains(regras, r => r.IdentityReference.Equals(eu) && r.FileSystemRights.HasFlag(FileSystemRights.Modify));
        Assert.Contains(regras, r => r.IdentityReference.Equals(new SecurityIdentifier(WellKnownSidType.LocalSystemSid, null)));
        Assert.DoesNotContain(regras, r => r.IdentityReference.Equals(new SecurityIdentifier(WellKnownSidType.BuiltinUsersSid, null)));
        Assert.DoesNotContain(regras, r => r.IdentityReference.Equals(new SecurityIdentifier(WellKnownSidType.AuthenticatedUserSid, null)));

        // quem foi liberado grava e lê sem ser administrador
        File.WriteAllText(Path.Combine(_pasta, "config.json"), "{}");
        Assert.Equal("{}", File.ReadAllText(Path.Combine(_pasta, "config.json")));
    }
}
