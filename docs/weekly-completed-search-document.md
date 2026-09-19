# Weekly Completed Search Document

This app now emits one JSON artifact per weekly run for completed work.

## Purpose

- Use the JSON as the source of truth for weekly completed work.
- Use the `content` field as the text sent to `text-embedding-3-small`.
- Store the resulting vector alongside the document fields in Azure AI Search.

## Output location

- `src/ConsoleApp/bin/Debug/net10.0/artifacts/weekly-completed/{documentId}.json`

## Document shape

```json
{
  "id": "weekly-jane-doe-20260726-20260802",
  "documentType": "weeklyCompletedSummary",
  "author": "Jane Doe",
  "weekStartUtc": "2026-07-26T00:00:00Z",
  "weekEndUtc": "2026-08-02T00:00:00Z",
  "completedPullRequestCount": 2,
  "pullRequestIds": [1234, 1248],
  "workItemIds": ["567890", "567912"],
  "completedPullRequests": [
    {
      "pullRequestId": 1234,
      "title": "Add weekly summary export",
      "description": "Creates a structured weekly completed-work artifact.",
      "repository": "StatusUpdaterApp",
      "branch": "refs/heads/feature/weekly-export",
      "createdDateUtc": "2026-07-28T09:30:00Z",
      "lastUpdatedAtUtc": "2026-07-30T18:15:00Z",
      "workItemIds": ["567890"],
      "resolvedComments": [
        "Please rename this method for clarity."
      ]
    }
  ],
  "content": "Weekly completed work summary for Jane Doe ..."
}
```

## Azure AI Search mapping

- `id`: key field
- `documentType`: filterable string
- `author`: searchable, filterable string
- `weekStartUtc`: sortable, filterable datetime
- `weekEndUtc`: sortable, filterable datetime
- `completedPullRequestCount`: filterable integer
- `pullRequestIds`: collection field
- `workItemIds`: searchable/filterable collection field
- `completedPullRequests`: store as complex collection if needed for retrieval
- `content`: searchable string to embed
- `contentVector`: vector field populated after embedding `content`

## Suggested pipeline

1. Generate weekly JSON with the console app.
2. Read `content` from each JSON document.
3. Send `content` to `text-embedding-3-small`.
4. Write the embedding into `contentVector`.
5. Upload the full document to Azure AI Search.

## Notes

- The JSON is stable enough for indexing and flexible enough to keep PR-level detail.
- If you later want richer retrieval, add commit summaries or work item titles to `completedPullRequests` and `content`.