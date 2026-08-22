import { readFile } from "node:fs/promises";
import { getEncoding } from "js-tiktoken";

const argumentsByName = new Map(
  process.argv.slice(2).reduce((pairs, value, index, values) => {
    if (value.startsWith("--")) {
      pairs.push([value, values[index + 1]]);
    }

    return pairs;
  }, []),
);
const textArgument = argumentsByName.get("--text");
const fileArgument = argumentsByName.get("--file");

if ((textArgument === undefined && fileArgument === undefined) ||
    (textArgument !== undefined && fileArgument !== undefined)) {
  throw new Error("Usage: node measure-tokens.mjs (--text <text> | --file <utf8-file>)");
}

const text = textArgument ?? await readFile(fileArgument, "utf8");
const encoding = getEncoding("o200k_base");

console.log(JSON.stringify({
  encoding: "o200k_base",
  characters: text.length,
  tokens: encoding.encode(text).length,
}, null, 2));
