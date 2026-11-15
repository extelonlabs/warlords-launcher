# SignPath setup for Warlords Launcher

GitHub Actions builds the public C#/WPF launcher on a GitHub-hosted Windows runner. The build uploads `Warlords.exe` as an unsigned workflow artifact. The SignPath Foundation application covers the launcher executable. The separately downloaded StratLords client package and its DLLs are outside this application.

The original launcher code is MIT-licensed. Its [artwork notice](../src/Warlords.Launcher/Assets/NOTICE.md) records the separate rights for the embedded crest, icon and hero image. Disclose these assets to the Foundation and confirm that the proposed artifact is eligible before enabling release signing. [Foundation terms](https://signpath.org/terms)

## After Foundation approval

1. In SignPath, add the GitHub.com Trusted Build System and link the project to `extelonlabs/warlords-launcher`. Restrict release signing to `StratLords` and `.github/workflows/build-launcher.yml`. Use a release policy that requires manual approval. [SignPath GitHub setup](https://docs.signpath.io/trusted-build-systems/github)
2. Create a release artifact configuration with a required `version` parameter. The GitHub artifact is a ZIP containing `Warlords.exe` at its root. [Artifact configuration syntax](https://docs.signpath.io/artifact-configuration/syntax)

   ```xml
   <artifact-configuration xmlns="http://signpath.io/artifact-configuration/v1">
     <parameters>
       <parameter name="version" required="true" />
     </parameters>
     <zip-file>
       <pe-file path="Warlords.exe" product-name="Warlords" product-version="${version}">
         <authenticode-sign />
       </pe-file>
     </zip-file>
   </artifact-configuration>
   ```

3. In GitHub Actions settings, add `SIGNPATH_API_TOKEN` as a repository secret for the SignPath submitter. Add `SIGNPATH_ORGANIZATION_ID`, `SIGNPATH_PROJECT_SLUG`, `SIGNPATH_SIGNING_POLICY_SLUG` and `SIGNPATH_ARTIFACT_CONFIGURATION_SLUG` as repository variables. Keep the signing approval in SignPath.
4. Run **Build Warlords Launcher** from `StratLords` with the signing option selected. Approve the exact revision in SignPath. Download the signed workflow artifact after GitHub verifies the Authenticode signature and timestamp.
5. Scan the signed executable and use its new SHA-256 for any release and launcher update manifest. The workflow uploads build artifacts to GitHub Actions. Publishing an update is a separate release operation.
