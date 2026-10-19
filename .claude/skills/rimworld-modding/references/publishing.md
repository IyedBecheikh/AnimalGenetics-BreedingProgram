# Publishing a mod on GitHub

- `.gitignore`: reference clones of other people's code (`_ref/`), `obj/`, `bin/`, harness build output, `.vs/`.
  **Do commit the built mod DLL** in `1.6/Assemblies/`, so the repo is installable as-is.
- Scan for secrets before the first commit (`gho_`, `sk-`, API key env names, "password").
- README sections that worked: one-paragraph pitch; preview image; features grouped by progression with screenshots
  (crop them from harness screenshots); requirements and load order; a compatibility table that only claims what
  was tested (e.g. "full suite and UI smoke test pass with zero errors"); getting started; known limitations;
  building from source (`-p:RimWorldDir=...` overrides the csproj property); testing (how to run the harness);
  credits to the base mod and art references; license.
- `About.xml`: `author` = GitHub username, `url` = repo, `modVersion`, dependency `downloadUrl`s, and a BBCode-style
  description. `About/Preview.png` at 1280×720: a collage of the main window + key icons + title.
- License: MIT unless the user says otherwise; mention it.
- `gh repo create OWNER/NAME --public --source=. --remote=origin --push --description "..."`, then
  `gh repo edit --add-topic rimworld --add-topic rimworld-mod ...`, then `gh release create v1.0.0 Mod-1.0.0.zip --notes-file ...`.
  Build the zip with a top-level mod folder containing only `About/ 1.6/ Languages/ Textures/ LICENSE README.md`.
- Commit messages and release notes: follow the session's attribution instructions.
- When the shell can't write to a full C: drive, even pipes to `head`/`cat` can fail. Redirect command output to a
  file on another drive and read that file.
