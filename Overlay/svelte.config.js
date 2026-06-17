import adapter from '@sveltejs/adapter-static';
import preprocess from 'svelte-preprocess';

/** @type {import('@sveltejs/kit').Config} */
const config = {
	// Consult https://github.com/sveltejs/svelte-preprocess
	// for more information about preprocessors
	preprocess: preprocess(),

	kit: {
		adapter: adapter(),
		typescript: {
			config: (config) => {
				delete config.compilerOptions.importsNotUsedAsValues;
				delete config.compilerOptions.preserveValueImports;
				return config;
			}
		}
	}
};

export default config;
