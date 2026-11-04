"""Writes tools/store/AppxManifest.xml: the Store package's manifest, with every file type Petal reads.

    python tools/store/make_manifest.py

The identity values stay placeholders ($IDENTITY_NAME$ and the others); build-store.ps1 fills them in from
Partner Center's Product identity page. Run this again after adding a format: a test checks that the
manifest lists every extension in Formats.cs.
"""
import re
from pathlib import Path

root = Path(__file__).resolve().parents[2]
source = (root / 'src' / 'Petal.Core' / 'Formats' / 'Formats.cs').read_text(encoding='utf-8')

# new("jpg", "JPG", FormatFamily.Image, ["jpg", "jpeg", "jfif", "jpe"])
pattern = re.compile(r'new\("[^"]+",\s*"[^"]+",\s*FormatFamily\.(\w+),\s*\[([^\]]*)\]', re.S)
families: dict[str, list[str]] = {}
for family, extensions in pattern.findall(source):
    for ext in re.findall(r'"([^"]+)"', extensions):
        families.setdefault(family, []).append(ext.lower())

groups = [
    ('petal.pictures', 'PicturesName', ['Image', 'Vector']),
    ('petal.media', 'MediaName', ['Audio', 'Video']),
    ('petal.documents', 'DocumentsName', ['Pdf', 'Document', 'Text', 'Ebook', 'Slides', 'Spreadsheet', 'Subtitle']),
    ('petal.others', 'OthersName', ['Archive', 'Project', 'Palette', 'Font']),
]
placed = {f for _, _, fs in groups for f in fs}
missing = set(families) - placed
assert not missing, f'families without a group: {missing}'

associations = []
for name, resource, fams in groups:
    extensions = []
    for fam in fams:
        for ext in families.get(fam, []):
            # Windows associates single extensions only: .tar.gz files come as .gz.
            if '.' not in ext and ext not in extensions:
                extensions.append(ext)
    types = '\n'.join(f'              <uap:FileType>.{e}</uap:FileType>' for e in extensions)
    associations.append(f'''        <uap3:Extension Category="windows.fileTypeAssociation">
          <uap3:FileTypeAssociation Name="{name}">
            <uap:DisplayName>ms-resource:{resource}</uap:DisplayName>
            <uap:SupportedFileTypes>
{types}
            </uap:SupportedFileTypes>
            <uap2:SupportedVerbs>
              <uap3:Verb Id="convert" Parameters="--wheel &quot;%1&quot;">ms-resource:ConvertVerb</uap3:Verb>
            </uap2:SupportedVerbs>
          </uap3:FileTypeAssociation>
        </uap3:Extension>''')

manifest = f'''<?xml version="1.0" encoding="utf-8"?>
<!-- Written by tools/store/make_manifest.py; build-store.ps1 fills in the $…$ values. -->
<Package
  xmlns="http://schemas.microsoft.com/appx/manifest/foundation/windows10"
  xmlns:uap="http://schemas.microsoft.com/appx/manifest/uap/windows10"
  xmlns:uap2="http://schemas.microsoft.com/appx/manifest/uap/windows10/2"
  xmlns:uap3="http://schemas.microsoft.com/appx/manifest/uap/windows10/3"
  xmlns:uap5="http://schemas.microsoft.com/appx/manifest/uap/windows10/5"
  xmlns:desktop="http://schemas.microsoft.com/appx/manifest/desktop/windows10"
  xmlns:desktop4="http://schemas.microsoft.com/appx/manifest/desktop/windows10/4"
  xmlns:desktop6="http://schemas.microsoft.com/appx/manifest/desktop/windows10/6"
  xmlns:rescap="http://schemas.microsoft.com/appx/manifest/foundation/windows10/restrictedcapabilities"
  IgnorableNamespaces="uap uap2 uap3 uap5 desktop desktop4 desktop6 rescap">

  <Identity Name="$IDENTITY_NAME$" Publisher="$PUBLISHER$" Version="$VERSION$" ProcessorArchitecture="x64" />

  <Properties>
    <DisplayName>ms-resource:AppName</DisplayName>
    <PublisherDisplayName>$PUBLISHER_DISPLAY_NAME$</PublisherDisplayName>
    <Logo>Assets\\StoreLogo.png</Logo>
    <!-- Petal's settings, log and petals stay in the usual AppData folders, where Notepad and File Explorer
         open them (and where a copy installed from Petal's own setup keeps them too). -->
    <desktop6:FileSystemWriteVirtualization>disabled</desktop6:FileSystemWriteVirtualization>
    <!-- The quick toggles change Windows' own settings (dark mode, hidden files, file name extensions). -->
    <desktop6:RegistryWriteVirtualization>disabled</desktop6:RegistryWriteVirtualization>
  </Properties>

  <Dependencies>
    <TargetDeviceFamily Name="Windows.Desktop" MinVersion="10.0.19041.0" MaxVersionTested="10.0.26100.0" />
  </Dependencies>

  <Resources>
    <Resource Language="en-us" />
    <Resource Language="ar-sa" />
  </Resources>

  <Applications>
    <Application Id="Petal" Executable="Petal.exe" EntryPoint="Windows.FullTrustApplication">
      <uap:VisualElements DisplayName="ms-resource:AppName" Description="ms-resource:AppDescription"
        BackgroundColor="transparent" Square150x150Logo="Assets\\Square150x150Logo.png" Square44x44Logo="Assets\\Square44x44Logo.png">
        <uap:DefaultTile Wide310x150Logo="Assets\\Wide310x150Logo.png" Square71x71Logo="Assets\\SmallTile.png" Square310x310Logo="Assets\\LargeTile.png" ShortName="ms-resource:AppName" />
        <uap:SplashScreen Image="Assets\\SplashScreen.png" />
      </uap:VisualElements>
      <Extensions>
        <!-- Off until Petal's "Start with Windows" asks for it; the user can also turn it off in Task Manager. -->
        <desktop:Extension Category="windows.startupTask" Executable="Petal.exe" EntryPoint="Windows.FullTrustApplication">
          <desktop:StartupTask TaskId="PetalStartup" Enabled="false" DisplayName="ms-resource:AppName" />
        </desktop:Extension>
{chr(10).join(associations)}
      </Extensions>
    </Application>

    <!-- The command line: "petal" in a terminal. No tile of its own. -->
    <Application Id="PetalCommand" Executable="petal-cli.exe" EntryPoint="Windows.FullTrustApplication" desktop4:SupportsMultipleInstances="true">
      <uap:VisualElements DisplayName="ms-resource:CommandName" Description="ms-resource:CommandDescription" AppListEntry="none"
        BackgroundColor="transparent" Square150x150Logo="Assets\\Square150x150Logo.png" Square44x44Logo="Assets\\Square44x44Logo.png" />
      <Extensions>
        <uap5:Extension Category="windows.appExecutionAlias" Executable="petal-cli.exe" EntryPoint="Windows.FullTrustApplication">
          <uap5:AppExecutionAlias desktop4:Subsystem="console">
            <uap5:ExecutionAlias Alias="petal.exe" />
          </uap5:AppExecutionAlias>
        </uap5:Extension>
      </Extensions>
    </Application>
  </Applications>

  <Capabilities>
    <Capability Name="internetClient" />
    <!-- Sending to and scanning from a phone on the same Wi-Fi. -->
    <Capability Name="privateNetworkClientServer" />
    <rescap:Capability Name="runFullTrust" />
    <rescap:Capability Name="unvirtualizedResources" />
    <!-- Dictation, asking Petal out loud and screen recordings with the voice. -->
    <DeviceCapability Name="microphone" />
    <!-- The camera's bubble in screen recordings. -->
    <DeviceCapability Name="webcam" />
  </Capabilities>
</Package>
'''
out = Path(__file__).resolve().parent / 'AppxManifest.xml'
out.write_text(manifest, encoding='utf-8', newline='\r\n')
print(out, sum(len(v) for v in families.values()), 'extensions')
