const test = require('node:test');
const assert = require('node:assert/strict');
const fs = require('node:fs');
const os = require('node:os');
const path = require('node:path');
const { spawnSync } = require('node:child_process');

const launcher = fs.readFileSync(path.join(__dirname, '..', 'start.bat'), 'utf8');

function runLauncher(provider, exitCode = 0, extraSettings = {}) {
  const directory = fs.mkdtempSync(path.join(os.tmpdir(), 'flexyparse-launcher-'));
  try {
    // Exercise real cmd expansion and dotenv handling without starting Docker or a server.
    const script = launcher
      .replace(/^docker compose .*$/m, 'ver > nul')
      .replace(/^dotnet run (.*)$/m, `echo DOTNET_ARGS $1\r\ncmd /d /c exit ${exitCode}`)
      .replace(/^\s*pause\s*$/gm, 'rem pause disabled in test');
    fs.writeFileSync(path.join(directory, 'start.bat'), script);
    const settings = { ...extraSettings };
    if (provider !== undefined) settings.FLEXYPARSE_RECOGNITION_PROVIDER = provider;
    if (Object.keys(settings).length) {
      fs.writeFileSync(path.join(directory, '.env'),
        Object.entries(settings).map(([key, value]) => `${key}=${value}\r\n`).join(''));
    }
    const env = { ...process.env };
    for (const key of Object.keys(env)) {
      if (['flexyparse_recognition_provider', 'flexyparse__recognition__provider',
        'flexyparse__recognition__nemotron__executionmode'].includes(key.toLowerCase())) delete env[key];
    }
    return spawnSync(process.env.ComSpec || 'cmd.exe', ['/d', '/c', 'start.bat'], {
      cwd: directory, env, encoding: 'utf8', timeout: 10000
    });
  } finally {
    fs.rmSync(directory, { recursive: true, force: true });
  }
}

for (const provider of [undefined, 'OpenAi', 'RapidOcr', 'NemotronCPU', '2', 'NemotronGPU', 'nemotrongpu', '3']) {
  test(`Windows launcher uses unified runtime for ${provider ?? 'default'}`, {
    skip: process.platform !== 'win32'
  }, () => {
    const result = runLauncher(provider);
    assert.ifError(result.error);
    assert.equal(result.status, 0, result.stdout + result.stderr);
    const gpu = ['NemotronGPU', 'nemotrongpu', '3'].includes(provider);
    assert.match(result.stdout, new RegExp(`ONNX backend: ${gpu ? 'webgpu' : 'cpu'}`));
    assert.match(result.stdout, /launcher-webgpu-runtime"/);
    assert.doesNotMatch(result.stdout, /-p:EnableOnnxGpu=/);
    assert.match(result.stdout, /--no-launch-profile/);
    assert.match(result.stdout, /-- serve --urls http:\/\/localhost:/);
  });
}

for (const [alias, standard, gpu] of [['NemotronCPU', 'NemotronGPU', true], ['NemotronGPU', 'NemotronCPU', false]]) {
  test(`Windows launcher honors standard provider ${standard} over alias ${alias}`, {
    skip: process.platform !== 'win32'
  }, () => {
    const result = runLauncher(alias, 0, { FlexyParse__Recognition__Provider: standard });
    assert.ifError(result.error);
    assert.equal(result.status, 0, result.stdout + result.stderr);
    assert.match(result.stdout, new RegExp(`ONNX backend: ${gpu ? 'webgpu' : 'cpu'}`));
  });
}

test('Windows launcher ignores obsolete ExecutionMode when selecting the build', {
  skip: process.platform !== 'win32'
}, () => {
  const result = runLauncher('NemotronCPU', 0, { FlexyParse__Recognition__Nemotron__ExecutionMode: 'CudaFp16' });
  assert.ifError(result.error);
  assert.equal(result.status, 0, result.stdout + result.stderr);
  assert.match(result.stdout, /ONNX backend: cpu/);
});

test('Windows launcher propagates a failed dotnet exit code', {
  skip: process.platform !== 'win32'
}, () => {
  const result = runLauncher('NemotronGPU', 7);
  assert.ifError(result.error);
  assert.equal(result.status, 7, result.stdout + result.stderr);
  assert.match(result.stdout, /Start failed with code 7/);
});
