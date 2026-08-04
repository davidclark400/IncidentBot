import { defineConfig } from '@hey-api/openapi-ts'

export default defineConfig({
  input: './openapi/panko-openapi.json',
  output: {
    clean: true,
    path: './src/api-client',
  },
  plugins: [
    {
      comments: false,
      enums: false,
      name: '@hey-api/typescript',
    },
  ],
})
