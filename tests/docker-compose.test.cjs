const assert = require("node:assert/strict");
const { spawnSync } = require("node:child_process");
const fs = require("node:fs");
const os = require("node:os");
const path = require("node:path");
const { after, test } = require("node:test");

const root = path.resolve(__dirname, "..");
const docker = spawnSync("docker", ["compose", "version"], { encoding: "utf8" });
const available = docker.status === 0;

// Keep developer settings out of config output and provide a known setting
// for the environment-file loading regression test.
const fixtureRoot = fs.mkdtempSync(path.join(os.tmpdir(), "flexyparse-compose-"));
after(() => fs.rmSync(fixtureRoot, { recursive: true, force: true }));
fs.mkdirSync(path.join(fixtureRoot, "docker"));
for (const file of ["docker-compose.yml", "docker-compose.gpu.yml", "docker-compose.image.yml", "docker-compose.image.gpu.yml"]) {
    fs.copyFileSync(path.join(root, "docker", file), path.join(fixtureRoot, "docker", file));
}
fs.copyFileSync(path.join(root, ".env.example"), path.join(fixtureRoot, ".env.example"));
fs.writeFileSync(path.join(fixtureRoot, ".env"), "FLEXYPARSE_COMPOSE_TEST_SETTING=fixture\n");

function config(files, variables = {}, { directory = fixtureRoot, resolveEnvironment = false } = {}) {
    const result = spawnSync("docker", [
        "compose", "--env-file", ".env.example",
        ...files.flatMap(file => ["-f", file]),
        "config", ...(resolveEnvironment ? [] : ["--no-env-resolution"]), "--format", "json"
    ], {
        cwd: directory,
        encoding: "utf8",
        env: { ...process.env, FLEXYPARSE_GPU_RENDER_GID: "109", ...variables }
    });
    assert.equal(result.status, 0, result.stderr);
    return JSON.parse(result.stdout);
}

function assertStack(stack, gpu, published = false) {
    const { flexyparse, gotenberg } = stack.services;
    assert.deepEqual(Object.keys(stack.services).sort(), ["flexyparse", "gotenberg"]);
    assert.equal(stack.name, "flexyparse");
    if (published) {
        assert.equal(flexyparse.build, undefined);
        assert.equal(flexyparse.image, `ghcr.io/ambyte/flexyparse:latest${gpu ? "-gpu" : ""}`);
    } else {
        assert.equal(flexyparse.build.context.replaceAll("\\", "/"), fixtureRoot.replaceAll("\\", "/"));
        assert.equal(flexyparse.build.dockerfile, gpu ? "docker/Dockerfile.gpu" : "docker/Dockerfile");
    }
    assert.equal(flexyparse.restart, "unless-stopped");
    assert.equal(flexyparse.depends_on.gotenberg.condition, "service_healthy");
    assert.equal(flexyparse.environment.FLEXYPARSE_GOTENBERG_URL, "http://gotenberg:3000");
    assert.ok(flexyparse.ports.some(port => port.target === 8080));
    assert.ok(flexyparse.volumes.some(volume => volume.source === "layout-models" && volume.target === "/app/models"));
    assert.ok(stack.volumes["layout-models"]);
    assert.equal(gotenberg.image, "gotenberg/gotenberg:8");
    assert.equal(gotenberg.restart, "unless-stopped");
    assert.ok(gotenberg.healthcheck.test.includes("http://localhost:3000/health"));
    assert.ok(gotenberg.ports.every(port => port.host_ip === "127.0.0.1"));
    if (gpu) {
        assert.equal(flexyparse.environment.FlexyParse__Recognition__Provider, "NemotronGPU");
        assert.equal(flexyparse.environment.FLEXYPARSE_RECOGNITION_PROVIDER, "NemotronGPU");
        assert.ok(flexyparse.devices.some(device => device.source === "/dev/dri" && device.target === "/dev/dri"));
        assert.ok(flexyparse.group_add.includes("109"));
    } else {
        assert.equal(flexyparse.devices, undefined);
        assert.equal(flexyparse.group_add, undefined);
    }
}

test("CPU Compose includes a healthy Gotenberg dependency and persistent models", { skip: !available }, () => {
    assertStack(config(["docker/docker-compose.image.yml"]), false);
});

test("GPU Compose runs standalone with Gotenberg and the shared model cache", { skip: !available }, () => {
    assertStack(config(["docker/docker-compose.image.gpu.yml"]), true);
});

test("GPU Compose remains compatible with the previous two-file command", { skip: !available }, () => {
    assertStack(config(["docker/docker-compose.image.yml", "docker/docker-compose.image.gpu.yml"]), true);
});

test("GPU Compose rejects a missing render group before starting containers", { skip: !available }, () => {
    const result = spawnSync("docker", [
        "compose", "--env-file", ".env.example", "-f", "docker/docker-compose.image.gpu.yml", "config", "--quiet"
    ], {
        cwd: fixtureRoot,
        encoding: "utf8",
        env: { ...process.env, FLEXYPARSE_GPU_RENDER_GID: "" }
    });
    assert.notEqual(result.status, 0);
    assert.match(result.stderr, /FLEXYPARSE_GPU_RENDER_GID/);
});

test("both stacks preserve explicit Gotenberg disable and share its timeout", { skip: !available }, () => {
    for (const file of ["docker-compose.image.yml", "docker-compose.image.gpu.yml", "docker-compose.yml", "docker-compose.gpu.yml"]) {
        const stack = config([`docker/${file}`], {
            FLEXYPARSE_GOTENBERG_URL: "",
            FLEXYPARSE_GOTENBERG_TIMEOUT_SECONDS: "240"
        });
        assert.equal(stack.services.flexyparse.environment.FLEXYPARSE_GOTENBERG_URL, "");
        assert.ok(stack.services.gotenberg.command.includes("--api-timeout=240s"));
    }
});

for (const gpu of [false, true]) {
    const file = `docker/docker-compose${gpu ? ".gpu" : ""}.yml`;
    test(`published ${gpu ? "GPU" : "CPU"} stack runs without a build context`, { skip: !available }, () => {
        assertStack(config([file]), gpu, true);
    });
    test(`published ${gpu ? "GPU" : "CPU"} stack supports version and registry overrides`, { skip: !available }, () => {
        const stack = config([file], {
            FLEXYPARSE_IMAGE: "ghcr.io/example/flexyparse",
            FLEXYPARSE_IMAGE_TAG: "1.2.3"
        });
        assert.equal(stack.services.flexyparse.image, `ghcr.io/example/flexyparse:1.2.3${gpu ? "-gpu" : ""}`);
        assert.equal(stack.services.flexyparse.build, undefined);
    });
}

test("all stacks load service settings from the root environment file", { skip: !available }, () => {
    for (const file of ["docker-compose.yml", "docker-compose.gpu.yml", "docker-compose.image.yml", "docker-compose.image.gpu.yml"]) {
        const stack = config([`docker/${file}`], {}, { resolveEnvironment: true });
        assert.equal(stack.services.flexyparse.environment.FLEXYPARSE_COMPOSE_TEST_SETTING, "fixture");
    }
});

test("all stacks allow a missing optional root environment file", { skip: !available }, () => {
    const directory = fs.mkdtempSync(path.join(os.tmpdir(), "flexyparse-compose-no-env-"));
    try {
        fs.cpSync(path.join(fixtureRoot, "docker"), path.join(directory, "docker"), { recursive: true });
        fs.copyFileSync(path.join(fixtureRoot, ".env.example"), path.join(directory, ".env.example"));
        for (const file of ["docker-compose.yml", "docker-compose.gpu.yml", "docker-compose.image.yml", "docker-compose.image.gpu.yml"]) {
            const stack = config([`docker/${file}`], {}, { directory, resolveEnvironment: true });
            assert.equal(stack.services.flexyparse.environment.FLEXYPARSE_GOTENBERG_URL, "http://gotenberg:3000");
            assert.equal(stack.services.flexyparse.environment.FLEXYPARSE_COMPOSE_TEST_SETTING, undefined);
        }
    } finally {
        fs.rmSync(directory, { recursive: true, force: true });
    }
});
