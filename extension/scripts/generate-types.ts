import { mkdir, readFile, writeFile } from 'node:fs/promises';
import { resolve } from 'node:path';
import { fileURLToPath } from 'node:url';
import { compile } from 'json-schema-to-typescript';

const extensionRoot = fileURLToPath(new URL('..', import.meta.url));
const schemaPath = resolve(extensionRoot, '../docs/protocol/kasi-messages.schema.json');
const outputPath = resolve(extensionRoot, 'src/types/protocol.ts');
const schema = {
  ...(JSON.parse(await readFile(schemaPath, 'utf8')) as object),
  title: 'KasiProtocolMessage',
};

const generated = await compile(schema, 'KasiProtocolMessage', {
  bannerComment: '/* Generated from docs/protocol/kasi-messages.schema.json. Do not edit. */',
  style: {
    singleQuote: true,
    printWidth: 100,
    endOfLine: 'crlf',
  },
});

const output = generated.replace(/\r?\n/g, '\r\n');

if (process.argv.includes('--check')) {
  let current: string;
  try {
    current = await readFile(outputPath, 'utf8');
  } catch {
    console.error('Generated protocol types are missing. Run npm run generate:types.');
    process.exitCode = 1;
    current = '';
  }

  // Git may check out LF on another host even though this repo edits with CRLF.
  if (current.replace(/\r\n/g, '\n') !== output.replace(/\r\n/g, '\n')) {
    console.error('Generated protocol types differ from the schema. Run npm run generate:types.');
    process.exitCode = 1;
  }
} else {
  await mkdir(resolve(extensionRoot, 'src/types'), { recursive: true });
  await writeFile(outputPath, output, 'utf8');
}
