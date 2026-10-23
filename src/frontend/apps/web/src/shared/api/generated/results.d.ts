// Generated from contracts/http/results/openapi.json. Run npm run generate:api; do not edit.
export interface paths {
    "/api/v1/results/tournaments/{tournamentId}/standings": {
        parameters: {
            query?: never;
            header?: never;
            path?: never;
            cookie?: never;
        };
        /** @description Anonymous, consistent published snapshot. Pending cells have no score; Results owns totals and places. Response excludes material, reviewer, assignment, appeal history and private basis data. */
        get: operations["getTournamentStandings"];
        put?: never;
        post?: never;
        delete?: never;
        options?: never;
        head?: never;
        patch?: never;
        trace?: never;
    };
    "/api/v1/results/tournaments/{tournamentId}/standings/events": {
        parameters: {
            query?: never;
            header?: never;
            path?: never;
            cookie?: never;
        };
        /** @description Anonymous Server-Sent Events invalidating a public snapshot. Register the subscription before reading the current snapshot. Events carry only tournamentId, snapshotVersion and technical connection state; reconnect requires a fresh GET. This stream is not a replay log or the source of result data. */
        get: operations["streamStandingsInvalidations"];
        put?: never;
        post?: never;
        delete?: never;
        options?: never;
        head?: never;
        patch?: never;
        trace?: never;
    };
    "/api/v1/results/me/tournaments/{tournamentId}/results": {
        parameters: {
            query?: never;
            header?: never;
            path?: never;
            cookie?: never;
        };
        /** @description Private results for the team bound to the authenticated server-side subject. appealAvailabilityHint is non-authoritative; Appeals alone decides whether submission is allowed. */
        get: operations["getMyTournamentResults"];
        put?: never;
        post?: never;
        delete?: never;
        options?: never;
        head?: never;
        patch?: never;
        trace?: never;
    };
    "/internal/v1/results/{resultId}/appeal-basis": {
        parameters: {
            query?: never;
            header?: never;
            path?: never;
            cookie?: never;
        };
        /** @description Trusted Appeals-service read of the currently published immutable basis. Must read the current record, not a cache. Does not authorize an appeal. */
        get: operations["getPublishedAppealBasis"];
        put?: never;
        post?: never;
        delete?: never;
        options?: never;
        head?: never;
        patch?: never;
        trace?: never;
    };
    "/health/live": {
        parameters: {
            query?: never;
            header?: never;
            path?: never;
            cookie?: never;
        };
        get: operations["getResultsLiveness"];
        put?: never;
        post?: never;
        delete?: never;
        options?: never;
        head?: never;
        patch?: never;
        trace?: never;
    };
    "/health/ready": {
        parameters: {
            query?: never;
            header?: never;
            path?: never;
            cookie?: never;
        };
        get: operations["getResultsReadiness"];
        put?: never;
        post?: never;
        delete?: never;
        options?: never;
        head?: never;
        patch?: never;
        trace?: never;
    };
}
export type webhooks = Record<string, never>;
export interface components {
    schemas: {
        /** @description Opaque identifier; do not derive rights or ordering from its value. */
        OpaqueId: string;
        /** @description Exact positive decimal integer; never pass through JavaScript Number. */
        PositiveVersion: string;
        /** @description Exact non-negative decimal snapshot version; never pass through JavaScript Number. */
        SnapshotVersion: string;
        /** @description Exact non-negative integer score in decimal notation. */
        Score: string;
        ProblemDetails: {
            type: string;
            title: string;
            status: number;
            code: string;
            correlationId: components["schemas"]["OpaqueId"];
        };
        StandingTask: {
            taskId: components["schemas"]["OpaqueId"];
            name: string;
        };
        StandingCell: {
            /** @enum {unknown} */
            status: "Pending" | "Published";
            score?: components["schemas"]["Score"];
        } & (unknown & unknown);
        StandingTeam: {
            teamId: components["schemas"]["OpaqueId"];
            name: string;
            cells: components["schemas"]["StandingCell"][];
            total: components["schemas"]["Score"];
            rank: number;
        };
        StandingsSnapshot: {
            tournamentId: components["schemas"]["OpaqueId"];
            snapshotVersion: components["schemas"]["SnapshotVersion"];
            tasks: components["schemas"]["StandingTask"][];
            teams: components["schemas"]["StandingTeam"][];
        };
        MyTaskResult: {
            taskId: components["schemas"]["OpaqueId"];
            resultId: components["schemas"]["OpaqueId"];
            resultVersion: components["schemas"]["PositiveVersion"];
            publicationId: components["schemas"]["OpaqueId"];
            /** @enum {unknown} */
            outcome: "Correct" | "Incorrect";
            score: components["schemas"]["Score"];
            /** @description Non-authoritative display hint only; Appeals checks permission and eligibility on submission. */
            appealAvailabilityHint: string;
        };
        MyTournamentResults: {
            tournamentId: components["schemas"]["OpaqueId"];
            results: components["schemas"]["MyTaskResult"][];
        };
        AppealBasis: {
            resultId: components["schemas"]["OpaqueId"];
            resultVersion: components["schemas"]["PositiveVersion"];
            tournamentId: components["schemas"]["OpaqueId"];
            teamId: components["schemas"]["OpaqueId"];
            taskId: components["schemas"]["OpaqueId"];
            solutionId: components["schemas"]["OpaqueId"];
            materialVersionId: components["schemas"]["OpaqueId"];
            sourceCheckId: components["schemas"]["OpaqueId"];
            /** @enum {unknown} */
            decisionOrigin: "OrdinaryReview" | "AppealReview" | "SeniorEscalation";
            /** @enum {unknown} */
            outcome: "Correct" | "Incorrect";
            scoreRuleVersion: components["schemas"]["OpaqueId"];
            publicationId: components["schemas"]["OpaqueId"];
            /** Format: date-time */
            publishedAt: string;
            /** @constant */
            publicationState: "Published";
        };
    };
    responses: {
        /** @description 401: Unauthorized. Missing or invalid credential. */
        Unauthorized: {
            headers: {
                "Cache-Control": components["headers"]["PrivateCache"];
                [name: string]: unknown;
            };
            content: {
                "application/problem+json": components["schemas"]["ProblemDetails"];
            };
        };
        /** @description 403: Forbidden. Authenticated subject/service lacks the required scope. */
        Forbidden: {
            headers: {
                "Cache-Control": components["headers"]["PrivateCache"];
                [name: string]: unknown;
            };
            content: {
                "application/problem+json": components["schemas"]["ProblemDetails"];
            };
        };
        /** @description 404: NotFound. Tournament is not visible or unknown. */
        NotFound: {
            headers: {
                "Cache-Control": components["headers"]["PrivateCache"];
                [name: string]: unknown;
            };
            content: {
                "application/problem+json": components["schemas"]["ProblemDetails"];
            };
        };
        /** @description 404: PublishedResultNotFound. No currently published result exists for this ID. */
        PublishedResultNotFound: {
            headers: {
                "Cache-Control": components["headers"]["PrivateCache"];
                [name: string]: unknown;
            };
            content: {
                "application/problem+json": components["schemas"]["ProblemDetails"];
            };
        };
        /** @description 409: ResultVersionConflict. The current published version differs; the caller is not rebased. */
        ResultVersionConflict: {
            headers: {
                "Cache-Control": components["headers"]["PrivateCache"];
                [name: string]: unknown;
            };
            content: {
                "application/problem+json": components["schemas"]["ProblemDetails"];
            };
        };
        /** @description 503: DependencyUnavailable. Temporary dependency failure; no business permission or receipt is inferred. */
        Unavailable: {
            headers: {
                "Cache-Control": components["headers"]["PrivateCache"];
                [name: string]: unknown;
            };
            content: {
                "application/problem+json": components["schemas"]["ProblemDetails"];
            };
        };
    };
    parameters: {
        TournamentId: components["schemas"]["OpaqueId"];
    };
    requestBodies: never;
    headers: {
        /** @description Strong validator for this coherent public snapshot. */
        SnapshotETag: string;
        /** @description Public representation; contains no private data. */
        PublicCache: "public, max-age=0, must-revalidate";
        /** @description Do not cache a live event stream. */
        NoCache: "no-cache";
        /** @description Private/internal response and errors are never stored by shared caches. */
        PrivateCache: "private, no-store";
    };
    pathItems: never;
}
export type $defs = Record<string, never>;
export interface operations {
    getTournamentStandings: {
        parameters: {
            query?: never;
            header?: never;
            path: {
                tournamentId: components["parameters"]["TournamentId"];
            };
            cookie?: never;
        };
        requestBody?: never;
        responses: {
            /** @description Current public standings snapshot */
            200: {
                headers: {
                    ETag: components["headers"]["SnapshotETag"];
                    "Cache-Control": components["headers"]["PublicCache"];
                    [name: string]: unknown;
                };
                content: {
                    "application/json": components["schemas"]["StandingsSnapshot"];
                };
            };
            404: components["responses"]["NotFound"];
            503: components["responses"]["Unavailable"];
        };
    };
    streamStandingsInvalidations: {
        parameters: {
            query?: never;
            header?: {
                /** @description Ignored for business replay; reconnect performs a fresh snapshot handshake. */
                "Last-Event-ID"?: string;
            };
            path: {
                tournamentId: components["parameters"]["TournamentId"];
            };
            cookie?: never;
        };
        requestBody?: never;
        responses: {
            /** @description SSE invalidation stream */
            200: {
                headers: {
                    "Cache-Control": components["headers"]["NoCache"];
                    [name: string]: unknown;
                };
                content: {
                    "text/event-stream": string;
                };
            };
            404: components["responses"]["NotFound"];
            503: components["responses"]["Unavailable"];
        };
    };
    getMyTournamentResults: {
        parameters: {
            query?: never;
            header?: never;
            path: {
                tournamentId: components["parameters"]["TournamentId"];
            };
            cookie?: never;
        };
        requestBody?: never;
        responses: {
            /** @description Own published task results */
            200: {
                headers: {
                    "Cache-Control": components["headers"]["PrivateCache"];
                    [name: string]: unknown;
                };
                content: {
                    "application/json": components["schemas"]["MyTournamentResults"];
                };
            };
            401: components["responses"]["Unauthorized"];
            403: components["responses"]["Forbidden"];
            404: components["responses"]["NotFound"];
            503: components["responses"]["Unavailable"];
        };
    };
    getPublishedAppealBasis: {
        parameters: {
            query: {
                /** @description Exact decimal version from the caller; Results never rebases this request. */
                expectedResultVersion: components["schemas"]["PositiveVersion"];
            };
            header?: never;
            path: {
                resultId: components["schemas"]["OpaqueId"];
            };
            cookie?: never;
        };
        requestBody?: never;
        responses: {
            /** @description Current published basis */
            200: {
                headers: {
                    "Cache-Control": components["headers"]["PrivateCache"];
                    [name: string]: unknown;
                };
                content: {
                    "application/json": components["schemas"]["AppealBasis"];
                };
            };
            401: components["responses"]["Unauthorized"];
            403: components["responses"]["Forbidden"];
            404: components["responses"]["PublishedResultNotFound"];
            409: components["responses"]["ResultVersionConflict"];
            503: components["responses"]["Unavailable"];
        };
    };
    getResultsLiveness: {
        parameters: {
            query?: never;
            header?: never;
            path?: never;
            cookie?: never;
        };
        requestBody?: never;
        responses: {
            /** @description Process is alive */
            200: {
                headers: {
                    [name: string]: unknown;
                };
                content?: never;
            };
        };
    };
    getResultsReadiness: {
        parameters: {
            query?: never;
            header?: never;
            path?: never;
            cookie?: never;
        };
        requestBody?: never;
        responses: {
            /** @description Results database is ready */
            200: {
                headers: {
                    [name: string]: unknown;
                };
                content?: never;
            };
            /** @description Results database is unavailable */
            503: {
                headers: {
                    [name: string]: unknown;
                };
                content?: never;
            };
        };
    };
}
