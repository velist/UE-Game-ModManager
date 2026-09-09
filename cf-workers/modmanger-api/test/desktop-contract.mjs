import { createRuntime } from "./runtime.mjs";
import { spawn } from "node:child_process";
import { mkdtemp, rm } from "node:fs/promises";
import { tmpdir } from "node:os";
import path from "node:path";
import { fileURLToPath } from "node:url";

const repository = fileURLToPath(new URL("../../../", import.meta.url));
const project = path.join(repository, "tools", "WorkerContractProbe", "WorkerContractProbe.csproj");
const providedArtifacts = process.env.UEMOD_CONTRACT_ARTIFACTS;
const artifacts = providedArtifacts
  ? path.resolve(providedArtifacts)
  : await mkdtemp(path.join(tmpdir(), "UEModManager-WorkerContract-"));

async function dotnet(args) {
  await new Promise((resolve, reject) => {
    const child = spawn("dotnet", args, { cwd: repository, stdio: "inherit", windowsHide: true });
    const timeout = setTimeout(() => {
      child.kill();
      reject(new Error("Desktop contract process exceeded three minutes"));
    }, 180000);
    child.once("error", error => { clearTimeout(timeout); reject(error); });
    child.once("exit", code => {
      clearTimeout(timeout);
      if (code === 0) resolve();
      else reject(new Error(`dotnet exited with code ${code}`));
    });
  });
}

let runtime;
try {
  runtime = await createRuntime();
  const url = (await runtime.mf.ready).toString();
  // Separate build + execution also works on SDK 8, whose dotnet run does not
  // consistently resolve executables when --artifacts-path is supplied.
  await dotnet(["build", project, "--configuration", "Release", "--artifacts-path", artifacts,
    "--nologo", "--verbosity", "quiet", "-warnaserror"]);
  await dotnet([path.join(artifacts, "bin", "WorkerContractProbe", "release", "WorkerContractProbe.dll"), url]);
  if (runtime.unexpected.length !== 0) throw new Error("Unexpected upstream requests were blocked");
} finally {
  if (runtime) await runtime.dispose();
  if (!providedArtifacts) {
    // This exact directory was created above for this run; never remove a caller-supplied path.
    const resolved = path.resolve(artifacts);
    const tempRoot = path.resolve(tmpdir()) + path.sep;
    if (!resolved.startsWith(tempRoot) || !path.basename(resolved).startsWith("UEModManager-WorkerContract-"))
      throw new Error("Refusing to clean an artifact path outside the owned temporary directory");
    await rm(resolved, { recursive: true, force: true, maxRetries: 5, retryDelay: 200 });
  }
}
