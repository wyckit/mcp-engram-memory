import importlib.util
from pathlib import Path
import tempfile
import unittest
import zipfile

spec = importlib.util.spec_from_file_location("packages", Path(__file__).resolve().parents[1] / "validate-packages.py")
packages = importlib.util.module_from_spec(spec)
spec.loader.exec_module(packages)


class PackageValidationTests(unittest.TestCase):
    version = "2.1.0"

    def setUp(self):
        self.temp = tempfile.TemporaryDirectory()
        self.addCleanup(self.temp.cleanup)
        self.feed = Path(self.temp.name)
        for name in packages.PACKAGES:
            files = {"README.md": b"readme", "icon.png": b"icon"}
            groups = []
            if name != "McpEngramMemory":
                for framework in packages.FRAMEWORKS:
                    files[f"lib/{framework}/{name}.dll"] = b"candidate-assembly"
                    files[f"lib/{framework}/{name}.xml"] = b"docs"
                    dependencies = (f'<dependency id="McpEngramMemory.Core" version="{self.version}" />'
                                    if name.endswith("Synthesis.Onnx") else
                                    f'<dependency id="System.Security.Cryptography.Pkcs" version="{"8.0.2" if framework == "net8.0" else "9.0.21"}" />'
                                    '<dependency id="SQLitePCLRaw.bundle_e_sqlite3" version="3.0.5" />')
                    groups.append(f'<group targetFramework="{framework}">{dependencies}</group>')
                if name.endswith("Core"):
                    for folder in ("build", "buildMultiTargeting"):
                        files[f"{folder}/{name}.targets"] = b"targets"
            else:
                prefix = "tools/net8.0/any/"
                files[prefix + "McpEngramMemory.Core.dll"] = b"candidate-assembly"
                files[prefix + "McpEngramMemory.runtimeconfig.json"] = b'{"runtimeOptions":{"tfm":"net8.0"}}'
                files[prefix + "DotnetToolSettings.xml"] = b'<DotNetCliTool><Commands><Command Name="engram-memory" EntryPoint="McpEngramMemory.dll" /></Commands></DotNetCliTool>'
                for rid in ("win-x64", "linux-x64", "osx-arm64"):
                    files[prefix + f"runtimes/{rid}/native/onnxruntime.dll"] = b"native"
            files[name + ".nuspec"] = (f'<package><metadata><id>{name}</id><version>{self.version}</version>'
                f'<releaseNotes>docs/release-notes-{self.version}.md</releaseNotes>'
                f'<dependencies>{"".join(groups)}</dependencies></metadata></package>').encode()
            self.write(name, files)

    def write(self, name, files):
        with zipfile.ZipFile(self.feed / f"{name}.{self.version}.nupkg", "w") as archive:
            for path, data in files.items():
                archive.writestr(path, data)

    def change(self, name, mutate):
        with zipfile.ZipFile(self.feed / f"{name}.{self.version}.nupkg") as archive:
            files = {f: archive.read(f) for f in archive.namelist()}
        mutate(files)
        self.write(name, files)

    def rejected(self):
        with self.assertRaises(AssertionError):
            packages.verify_packages(self.feed, self.version)

    def test_matching_set_passes_and_records_hashes(self):
        result = packages.verify_packages(self.feed, self.version)
        self.assertEqual(3, len(result))
        self.assertTrue(all(len(p["sha256"]) == 64 for p in result))

    def test_missing_package_fails(self):
        (self.feed / f"McpEngramMemory.Core.{self.version}.nupkg").unlink()
        self.rejected()

    def test_wrong_version_stale_notes_or_servicing_pin_fail(self):
        name = "McpEngramMemory.Core"
        for before, after in ((b'<version>2.1.0', b'<version>2.0.0'),
                              (b'release-notes-2.1.0', b'release-notes-2.0.0'),
                              (b'version="8.0.2"', b'version="8.0.1"')):
            with self.subTest(before=before):
                self.change(name, lambda f: f.update({name + ".nuspec": f[name + ".nuspec"].replace(before, after)}))
                self.rejected()
                self.change(name, lambda f: f.update({name + ".nuspec": f[name + ".nuspec"].replace(after, before)}))

    def test_old_core_dependency_fails(self):
        name = "McpEngramMemory.Synthesis.Onnx"
        self.change(name, lambda f: f.update({name + ".nuspec": f[name + ".nuspec"].replace(b'version="2.1.0"', b'version="2.0.0"')}))
        self.rejected()

    def test_missing_framework_assembly_fails(self):
        self.change("McpEngramMemory.Core", lambda f: f.pop("lib/net9.0/McpEngramMemory.Core.dll"))
        self.rejected()

    def test_stale_bundled_core_fails(self):
        self.change("McpEngramMemory", lambda f: f.update({"tools/net8.0/any/McpEngramMemory.Core.dll": b"old-assembly"}))
        self.rejected()

    def test_mobile_native_fails(self):
        self.change("McpEngramMemory", lambda f: f.update({"tools/net8.0/any/runtimes/ios-arm64/native/onnx": b"mobile"}))
        self.rejected()

    def test_missing_desktop_native_fails(self):
        self.change("McpEngramMemory", lambda f: f.pop("tools/net8.0/any/runtimes/osx-arm64/native/onnxruntime.dll"))
        self.rejected()


if __name__ == "__main__":
    unittest.main()
