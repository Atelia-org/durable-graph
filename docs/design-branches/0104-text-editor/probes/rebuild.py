"""Rebuild the DB-104 friend-bridge witness from pinned tracked source in a temp folder."""
import argparse
import os
from pathlib import Path
import shutil
import subprocess
import tempfile
from xml.sax.saxutils import escape

BASE = "93f5ece17a57cce4b0b2dda5c5d8fea15fa7e3c0"
FILES = (
    "DurableText.cs", "TextSnapshot.cs", "TextEdit.cs", "TextEditFailure.cs",
    "Runtime/Text/TextPieceView.cs", "Runtime/Text/TextEditBatch.cs",
    "Runtime/Text/DurableTextState.cs",
)
HERE = Path(__file__).resolve().parent

def baseline(repo, file):
    spec = f"{BASE}:src/DurableGraph/{file}"
    return subprocess.check_output(["git", "-C", repo, "show", spec]).decode("utf-8-sig")

def normalize(source):
    return (source.replace("namespace Atelia.DurableGraph;", "namespace Atelia.Text;")
            .replace("DurableTextAcceptance", "PreparedRebase")
            .replace("DurableTextState", "FrozenText").replace("DurableText", "TextEditor"))

def project(path, name, properties="", items="", exe=True):
    kind = "<OutputType>Exe</OutputType>" if exe else ""
    path.write_text(f"""<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup>
    <TargetFramework>net10.0</TargetFramework><AssemblyName>{name}</AssemblyName>
    <ImplicitUsings>enable</ImplicitUsings><Nullable>enable</Nullable>{kind}{properties}
  </PropertyGroup>
  <ItemGroup>{items}</ItemGroup>
</Project>
""", encoding="utf-8")

def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("repository", type=Path)
    parser.add_argument("--binary-directory", type=Path,
                        help="Compiled baseline Atelia.DurableGraph/Serialization/Binary Release DLLs")
    parser.add_argument("--bench", action="store_true", help="Run ordered baseline/candidate allocation batches")
    parser.add_argument("--alternatives", action="store_true",
                        help="Run the physical-slice comparison and three memory maintenance traces")
    args = parser.parse_args()
    repo = args.repository.resolve()
    dll = (args.binary_directory or repo/"src/DurableGraph/bin/Release/net10.0").resolve()
    target = Path(tempfile.mkdtemp(prefix="db104-friend-"))
    print(target, flush=True)
    for name in ["Core", "Adapter", "Original", "Bridged"]:
        (target/name).mkdir()
    for file in FILES:
        (target/"Core"/Path(file).name).write_text(normalize(baseline(repo,file)),encoding="utf-8", newline="\n")
    codec = baseline(repo,"Runtime/Text/TextBodyCodec.cs").replace("DurableTextState","FrozenText")
    (target/"Adapter/Codec.cs").write_text("using Atelia.Text;\n"+codec,encoding="utf-8", newline="\n")
    subprocess.run(["git", "apply", "--ignore-space-change", "--whitespace=nowarn", str(HERE/"bridge.patch")], cwd=target, check=True)
    project(target/"Core/Core.csproj", "Atelia.Text", items='<InternalsVisibleTo Include="Atelia.DurableGraph"/>',exe=False)
    refs = {}
    for name in ("Atelia.Binary", "Atelia.DurableGraph.Serialization", "Atelia.DurableGraph"):
        assembly=dll/f"{name}.dll"
        if not assembly.is_file(): raise FileNotFoundError(assembly)
        refs[name]=f'<Reference Include="{name}"><HintPath>{escape(str(assembly))}</HintPath></Reference>'
    libs=refs["Atelia.Binary"]+refs["Atelia.DurableGraph.Serialization"]
    link='<ProjectReference Include="../Core/Core.csproj"/>'
    project(target/"Adapter/Adapter.csproj", "Atelia.DurableGraph", items=libs+link)
    shutil.copyfile(HERE/"verify.cs", target/"Adapter/Program.cs")
    def run(cmd):
        subprocess.run(cmd,cwd=target,env={**os.environ, "DOTNET_TieredCompilation":"0"},check=True)
    run(["dotnet", "run", "--project", "Adapter/Adapter.csproj", "-c", "Release"])
    if args.alternatives:
        for comparison in ("SnapshotDiff", "PrefixSuffix"):
            diff = target / comparison
            diff.mkdir()
            project(diff / "Diff.csproj", "Atelia.DurableGraph.Tests", items=libs + refs["Atelia.DurableGraph"])
            shutil.copyfile(HERE / f"{comparison}.cs", diff / "Program.cs")
            run(["dotnet", "run", "--project", f"{comparison}/Diff.csproj", "-c", "Release"])

        memory = target / "Memory"
        memory.mkdir()
        project(memory / "Memory.csproj", "Memory", items=libs)
        for file in (*FILES, "Runtime/Text/TextBodyCodec.cs"):
            (memory / Path(file).name).write_text(baseline(repo, file), encoding="utf-8")
        shutil.copyfile(HERE / "Memory.cs", memory / "Program.cs")
        shutil.copyfile(HERE / "PreservingCompact.cs", memory / "PreservingCompact.cs")
        run(["dotnet", "build", "Memory/Memory.csproj", "-c", "Release"])
        for rotation in range(3):
            run(["dotnet", "Memory/bin/Release/net10.0/Memory.dll", str(rotation)])
    if args.bench:
        project(target/"Original/Bench.csproj", "Atelia.DurableGraph.Tests", "<DefineConstants>ORIGINAL</DefineConstants>",libs+refs["Atelia.DurableGraph"])
        project(target/"Bridged/Bench.csproj", "Atelia.DurableGraph", items=libs+link+'<Compile Include="../Adapter/Codec.cs" Link="Codec.cs"/>')
        for name in ("Original","Bridged"):
            shutil.copyfile(HERE/"bench.cs",target/name/"Program.cs")
            run(["dotnet","build",f"{name}/Bench.csproj","-c","Release","-v:q"])
        for name in ("Original","Bridged","Bridged","Original","Original","Bridged"):
            print(name,flush=True)
            assembly="Atelia.DurableGraph.Tests" if name=="Original" else "Atelia.DurableGraph"
            run(["dotnet",f"{name}/bin/Release/net10.0/{assembly}.dll"])

if __name__ == "__main__":
    main()
