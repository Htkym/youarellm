# Token measurement

`measure-tokens.mjs` counts UTF-8 text with `js-tiktoken` 1.0.21 and the
fixed `o200k_base` encoding.

## Procedure

1. Install the locked dependency:

   ```powershell
   Push-Location .\token
   npm ci
   ```

2. Measure either inline text or a UTF-8 text file:

   ```powershell
   node .\measure-tokens.mjs --text "Hello, world!"
   node .\measure-tokens.mjs --file .\prompt.txt
   Pop-Location
   ```

3. Record the encoding, tool version, input source, character count, and
   reported token count when comparing inputs.

The output is JSON with `encoding`, `characters`, and `tokens`.

## Scope

The count is a tokenizer estimate for the supplied text only. It excludes API
request framing, provider-side instructions, tool execution output not included
in the input, caching, and provider billing. Keep the encoding and package
version fixed when comparing measurements.
