import tseslint from "typescript-eslint";
import reactHooks from "eslint-plugin-react-hooks";

// One purpose: catch hooks used in the wrong place (after an early return, in a condition or a loop). TypeScript cannot see this mistake,
// and it makes a page crash only once its data has loaded.
export default tseslint.config(
  { ignores: ["dist", "node_modules"] },
  { files: ["src/**/*.{ts,tsx}"], languageOptions: { parser: tseslint.parser }, plugins: { "react-hooks": reactHooks }, rules: { "react-hooks/rules-of-hooks": "error" } },
);
