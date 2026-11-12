import fs from 'node:fs';
import path from 'node:path';

// Reads a C# type that is split into partial files (Name.cs plus Name.*.cs) as one text: the main
// file first, then the parts by name, then any companion files the type delegates to with "X.cs" ahead of "X.Sub.cs", so related parts stay adjacent.
export function readPartialType(file, companions = []) {
  const directory = path.dirname(file);
  const base = path.basename(file, '.cs');
  const parts = fs.readdirSync(directory)
    .filter((name) => name.startsWith(base + '.') && name.endsWith('.cs') && name !== base + '.cs')
    .sort((a, b) => {
      const ka = a.slice(0, -3);
      const kb = b.slice(0, -3);
      return ka < kb ? -1 : ka > kb ? 1 : 0;
    });
  return [file, ...parts.map((name) => path.join(directory, name)), ...companions.map((name) => path.join(directory, name))]
    .map((name) => fs.readFileSync(name, 'utf8'))
    .join('\n');
}
