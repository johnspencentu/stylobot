// ESM loader: remaps ./foo.js → ./foo.ts for source files under src/
// Enables `node --experimental-strip-types --import ./ts-loader.mjs --test` to work
// with TypeScript source files that use `.js` extensions in their imports.
export async function resolve(specifier, context, nextResolve) {
  // Only remap relative .js imports that come from a .ts source file
  if (specifier.endsWith('.js') && context.parentURL?.includes('/src/')) {
    const tsSpecifier = specifier.slice(0, -3) + '.ts';
    try {
      return await nextResolve(tsSpecifier, context);
    } catch {
      // Fall through to original if .ts doesn't exist
    }
  }
  return nextResolve(specifier, context);
}
