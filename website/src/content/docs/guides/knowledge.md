---
title: Knowledge Tab
description: Browse and search what the AI assistant has indexed from your repositories.
---

The **Knowledge** tab shows what your AI assistant service has indexed from your repositories: documentation,
ADRs, C# code and BPMN processes, each tied to the commit it came from. Use it to see what is indexed and what
an agent would find.

The tab is optional. It appears only when an [assistant service](/fleans/reference/assistant-backend-contract/#knowledge-index-optional)
is configured and offers a knowledge index; otherwise it isn't there at all. If the service is unreachable when
you open Fleans, reload the page once it is back.

You see only the repositories the assistant service lets you read, and nothing is shared between users. Use
the **Project** filter at the top to narrow all four views.

## Sources

The indexed repositories: project, repository, branch, short commit, when it was indexed, chunks per kind
(`doc`, `adr`, `code`, `bpmn`) and whether embeddings are on. *Not indexed yet* means the assistant has not
indexed any repository you may read.

## Search

Type a query and choose **Search** or press Enter; tick kinds to narrow it. Each hit shows its kind, its
`repo:path:line`, its branch and commit, a title or symbol and a snippet. Selecting the location opens the file
at that chunk, and **Back** returns to your results.

## Browse

Pick a repository and expand its folders. **Load more** appears on large repositories. Opening a file shows its
chunks in line order: documentation and ADRs as formatted text, C# as code, and BPMN as a process summary. If a
file has since left the index, or you have no access to it, the view says *Not found*.

## ADRs

The project's architecture decision records, with a search box of their own.

## Links to your git provider

When the assistant service knows a repository's web address, **Open in git** opens the file at the exact commit
that was indexed.
