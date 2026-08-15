import { generateKeyPairSync } from 'node:crypto';
import { mkdirSync, writeFileSync } from 'node:fs';
import { dirname, resolve } from 'node:path';
import { fileURLToPath } from 'node:url';

const root = resolve(dirname(fileURLToPath(import.meta.url)), '..');
const secretDirectory = resolve(root, '.secrets');
const publicDirectory = resolve(root, 'package/BepInEx/config/XomNghienBootstrap');
mkdirSync(secretDirectory, { recursive: true, mode: 0o700 });
mkdirSync(publicDirectory, { recursive: true });

const { privateKey, publicKey } = generateKeyPairSync('rsa', { modulusLength: 3072 });
const privatePem = privateKey.export({ format: 'pem', type: 'pkcs8' });
const jwk = publicKey.export({ format: 'jwk' });
const toBase64 = (value) => Buffer.from(value.replaceAll('-', '+').replaceAll('_', '/') + '='.repeat((4 - value.length % 4) % 4), 'base64').toString('base64');
const publicXml = `<RSAKeyValue><Modulus>${toBase64(jwk.n)}</Modulus><Exponent>${toBase64(jwk.e)}</Exponent></RSAKeyValue>\n`;

writeFileSync(resolve(secretDirectory, 'bootstrap-private-key.pem'), privatePem, { mode: 0o600 });
writeFileSync(resolve(publicDirectory, 'trusted-public-key.xml'), publicXml);
process.stdout.write([
  'Generated a new bootstrap signing key pair.',
  'Private key: .secrets/bootstrap-private-key.pem (gitignored; put it in deployment secrets)',
  'Public key: package/BepInEx/config/XomNghienBootstrap/trusted-public-key.xml',
  'Base64 for XN_BOOTSTRAP_SIGNING_PRIVATE_KEY_BASE64:',
  Buffer.from(privatePem).toString('base64'),
].join('\n') + '\n');
