# Engineering Assistant

This repository generates a weekly report for the Azure DevOps author alias `kanis`, stores it in Azure AI Search, and exposes the indexed reports to VS Code GitHub Copilot Chat through MCP.

## Run Locally

Use the interactive console:

```powershell
dotnet run --project src/ConsoleApp
```

Use the automation-friendly command:

```powershell
dotnet run --project src/ConsoleApp -- weekly --author kanis --end-date 2026-09-20 --publish
```

The end date is inclusive. An end date of Sunday, September 20 generates the Monday, September 14 through Sunday, September 20 report. `--publish` stores the report and its vectors in Azure AI Search. `COMPARE_RAG` controls only whether an additional historical comparison report is generated.

For an arbitrary inclusive period, also pass `--start-date`:

```powershell
dotnet run --project src/ConsoleApp -- weekly --author kanis --start-date 2026-09-10 --end-date 2026-09-19 --publish
```

## Weekly Automation

[`.github/workflows/weekly-kanis-report.yml`](.github/workflows/weekly-kanis-report.yml) runs every Monday at 02:00 UTC. It uses the previous Sunday as the inclusive end date, publishes the report, and retains the generated JSON as a GitHub Actions artifact for 90 days.

Configure these GitHub repository variables under **Settings > Secrets and variables > Actions > Variables**:

- `AZDO_ORG`
- `AZDO_PROJECT`
- `AZDO_REPO`
- `AZURE_OPENAI_ENDPOINT`
- `AZURE_OPENAI_DEPLOYMENT`
- `AZURE_OPENAI_EMBEDDING_DEPLOYMENT`
- `AZURE_OPENAI_API_VERSION`
- `AZURE_SEARCH_ENDPOINT`
- `AZURE_SEARCH_INDEX_NAME`

Configure these repository secrets under **Settings > Secrets and variables > Actions > Secrets**:

- `AZDO_PAT`
- `AZURE_OPENAI_API_KEY`
- `AZURE_SEARCH_API_KEY`

The Azure DevOps PAT needs read access to code, pull requests, and work items. The runner must have network access to Azure DevOps, Azure OpenAI, and Azure AI Search. Use a self-hosted runner if those Azure resources are private.

Test the workflow from **Actions > Weekly Kanis engineering report > Run workflow**. Optional start and end dates can be supplied for a backfill. Scheduled workflows run from the repository's default branch and use UTC.

The cron expression controls the automatic run day. The current value, `0 2 * * 1`, means every Monday at 02:00 UTC. Change the final field to `2` for Tuesday through `0` for Sunday. Keep the scheduled run after the reporting period ends because the default end date is the previous UTC day.

For a manual custom-period report, choose **Run workflow**, enter both `start_date` and `end_date`, and run it. Leaving `start_date` blank retains the seven-day behavior.

## Copilot Chat

The workspace MCP configuration is in [`.vscode/mcp.json`](.vscode/mcp.json). It launches `src/EngineeringAssistant.Mcp` locally and reads the gitignored `src/ConsoleApp/appsettings.local.json` file already used by the console app.

1. Open this repository in VS Code.
2. Ensure MCP support is enabled in the current VS Code/GitHub Copilot version.
3. Open the MCP servers view or run **MCP: List Servers**, then start `kanis-work-history` if it is not already running.
4. Open Copilot Chat and select the **Kanis Work History** custom agent.
5. Ask a question such as: `What SDK migration work did Kanishka complete in the last month?`

After pulling changes to the MCP server or its tools, restart `kanis-work-history` from **MCP: List Servers** so VS Code discovers the updated tool list.

To generate a new report for a period, prompt the same agent with explicit or relative dates, for example:

```text
Generate Kanishka's SDK v12 migration report from 2026-09-10 through 2026-09-19.
```

Generation does not publish by default. Add `and publish it` when the report should be stored in Azure AI Search for future history searches. The MCP server runs the local report generator, so VS Code must remain open for an agent-triggered generation; GitHub Actions handles unattended scheduled runs.

The agent converts relative dates to explicit bounds and chooses between generating a new report and searching indexed history. Search responses should cite report IDs, PR numbers, and work-item numbers.

The local MCP process is started on demand by VS Code; it does not need cloud hosting. GitHub Actions also uses temporary runners, so the report generator does not need continuous hosting.