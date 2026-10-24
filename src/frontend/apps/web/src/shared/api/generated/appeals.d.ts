// Generated from contracts/http/appeals/openapi.json. Run npm run generate:api; do not edit.
export interface paths {
    "/api/v1/appeals": {
        parameters: {
            query?: never;
            header?: never;
            path?: never;
            cookie?: never;
        };
        get?: never;
        put?: never;
        /** @description Registers one permanent appeal for the authenticated team/task/tournament tuple after Results confirms the current Published basis. teamId and author are taken from the verified server-side subject. Registration creates Registered only; it does not enqueue RecheckRequested or select a reviewer. */
        post: operations["submitAppeal"];
        delete?: never;
        options?: never;
        head?: never;
        patch?: never;
        trace?: never;
    };
    "/api/v1/appeals/me": {
        parameters: {
            query?: never;
            header?: never;
            path?: never;
            cookie?: never;
        };
        /** @description Returns only appeals belonging to the team bound to the verified subject. No raw broker payloads, tokens or material are included. */
        get: operations["getMyAppeals"];
        put?: never;
        post?: never;
        delete?: never;
        options?: never;
        head?: never;
        patch?: never;
        trace?: never;
    };
    "/api/v1/appeals/tournaments/{tournamentId}": {
        parameters: {
            query?: never;
            header?: never;
            path?: never;
            cookie?: never;
        };
        /** @description Jury queue of appeals awaiting manual selection. Registered is the only queue state exposed by this operation; read access does not claim or reserve an appeal. */
        get: operations["getRegisteredAppeals"];
        put?: never;
        post?: never;
        delete?: never;
        options?: never;
        head?: never;
        patch?: never;
        trace?: never;
    };
    "/api/v1/appeals/{appealId}": {
        parameters: {
            query?: never;
            header?: never;
            path?: never;
            cookie?: never;
        };
        /** @description Private process view for its team or authorized jury in scope. The ETag is the Appeal aggregate version, not a Checking or Results version. */
        get: operations["getAppeal"];
        put?: never;
        post?: never;
        delete?: never;
        options?: never;
        head?: never;
        patch?: never;
        trace?: never;
    };
    "/api/v1/appeals/{appealId}/claim": {
        parameters: {
            query?: never;
            header?: never;
            path?: never;
            cookie?: never;
        };
        get?: never;
        put?: never;
        /** @description Manually selects the authenticated jury member for one current Registered version and emits the same attempt through RecheckRequested. requestedReviewerId is server-derived; 202 means AcceptedAwaitingRecheck, not a confirmed Checking assignment. A ReviewerBusy response does not automatically retry or claim another reviewer. */
        post: operations["claimAppeal"];
        delete?: never;
        options?: never;
        head?: never;
        patch?: never;
        trace?: never;
    };
    "/dev/v1/tournaments/{tournamentId}/end-facts": {
        parameters: {
            query?: never;
            header?: never;
            path?: never;
            cookie?: never;
        };
        get?: never;
        put?: never;
        /** @description Local/Test-only adapter for a manually supplied tournament-end fact. Route is absent outside Local/Test. It closes only the Appeals submission window and does not provide tournament administration or scheduled closure. */
        post: operations["recordLocalTournamentEndFact"];
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
        get: operations["getAppealsLiveness"];
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
        get: operations["getAppealsReadiness"];
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
        /** @description Exact decimal version; never parse through JavaScript Number. */
        PositiveVersion: string;
        /** @enum {unknown} */
        Outcome: "Correct" | "Incorrect";
        /** @enum {unknown} */
        AppealState: "Registered" | "AcceptedAwaitingRecheck" | "RecheckPending" | "AwaitingSeniorReview" | "AwaitingResultApplication" | "Closed" | "NeedsResolution";
        ProblemDetails: {
            type: string;
            title: string;
            status: number;
            code: string;
            correlationId: components["schemas"]["OpaqueId"];
        };
        SubmitAppealRequest: {
            resultId: components["schemas"]["OpaqueId"];
            expectedResultVersion: components["schemas"]["PositiveVersion"];
        };
        EndFactRequest: {
            factId: components["schemas"]["OpaqueId"];
        };
        AppealSummary: {
            appealId: components["schemas"]["OpaqueId"];
            tournamentId: components["schemas"]["OpaqueId"];
            teamId: components["schemas"]["OpaqueId"];
            taskId: components["schemas"]["OpaqueId"];
            state: components["schemas"]["AppealState"];
            /** Format: date-time */
            submittedAt: string;
            version: components["schemas"]["PositiveVersion"];
        };
        AppealList: {
            tournamentId: components["schemas"]["OpaqueId"];
            items: components["schemas"]["AppealSummary"][];
        };
        AppealQueue: {
            tournamentId: components["schemas"]["OpaqueId"];
            items: components["schemas"]["AppealSummary"][];
        };
        AppealView: {
            appealId: components["schemas"]["OpaqueId"];
            tournamentId: components["schemas"]["OpaqueId"];
            teamId: components["schemas"]["OpaqueId"];
            taskId: components["schemas"]["OpaqueId"];
            state: components["schemas"]["AppealState"];
            version: components["schemas"]["PositiveVersion"];
            resultId: components["schemas"]["OpaqueId"];
            resultVersion: components["schemas"]["PositiveVersion"];
            baseCheckId: components["schemas"]["OpaqueId"];
            basePublicationId: components["schemas"]["OpaqueId"];
            /** @description Present only after a validated Applied receipt closes the Appeal. */
            finalPublicationId?: components["schemas"]["OpaqueId"];
            baseOutcome: components["schemas"]["Outcome"];
            /** Format: date-time */
            submittedAt: string;
            recheckRequestId?: components["schemas"]["OpaqueId"];
            checkId?: components["schemas"]["OpaqueId"];
        };
        ClaimAccepted: {
            appealId: components["schemas"]["OpaqueId"];
            /** @constant */
            state: "AcceptedAwaitingRecheck";
            version: components["schemas"]["PositiveVersion"];
        };
        ClosedWindow: {
            tournamentId: components["schemas"]["OpaqueId"];
            /** @constant */
            state: "Closed";
            version: components["schemas"]["PositiveVersion"];
            factId: components["schemas"]["OpaqueId"];
        };
    };
    responses: {
        /** @description 400: InvalidRequest. Request shape is invalid. */
        InvalidRequest: {
            headers: {
                "Cache-Control": components["headers"]["PrivateCache"];
                [name: string]: unknown;
            };
            content: {
                "application/problem+json": components["schemas"]["ProblemDetails"];
            };
        };
        /** @description 401: Unauthorized. Missing or invalid user token. */
        Unauthorized: {
            headers: {
                "Cache-Control": components["headers"]["PrivateCache"];
                [name: string]: unknown;
            };
            content: {
                "application/problem+json": components["schemas"]["ProblemDetails"];
            };
        };
        /** @description 403: Forbidden. Subject lacks role, team ownership, or tournament scope. */
        Forbidden: {
            headers: {
                "Cache-Control": components["headers"]["PrivateCache"];
                [name: string]: unknown;
            };
            content: {
                "application/problem+json": components["schemas"]["ProblemDetails"];
            };
        };
        /** @description 404: NotFound. Object is unknown or not visible to this subject. */
        NotFound: {
            headers: {
                "Cache-Control": components["headers"]["PrivateCache"];
                [name: string]: unknown;
            };
            content: {
                "application/problem+json": components["schemas"]["ProblemDetails"];
            };
        };
        /** @description 409: AlreadyAppealed, WindowClosed, BaseNotAppealable, ResultVersionConflict, IdempotencyConflict. */
        SubmitConflict: {
            headers: {
                "Cache-Control": components["headers"]["PrivateCache"];
                [name: string]: unknown;
            };
            content: {
                "application/problem+json": components["schemas"]["ProblemDetails"];
            };
        };
        /** @description 409: Conflict. The Appeal is not claimable in its current state, or a selection attempt is already pending. */
        ClaimConflict: {
            headers: {
                "Cache-Control": components["headers"]["PrivateCache"];
                [name: string]: unknown;
            };
            content: {
                "application/problem+json": components["schemas"]["ProblemDetails"];
            };
        };
        /** @description 412: AppealVersionConflict. The current version is not substituted automatically. */
        VersionConflict: {
            headers: {
                "Cache-Control": components["headers"]["PrivateCache"];
                [name: string]: unknown;
            };
            content: {
                "application/problem+json": components["schemas"]["ProblemDetails"];
            };
        };
        /** @description 428: IfMatchRequired. Claim requires one strong Appeal ETag. */
        IfMatchRequired: {
            headers: {
                "Cache-Control": components["headers"]["PrivateCache"];
                [name: string]: unknown;
            };
            content: {
                "application/problem+json": components["schemas"]["ProblemDetails"];
            };
        };
        /** @description 409: IdempotencyConflict. Same key with different normalized request payload. */
        IdempotencyConflict: {
            headers: {
                "Cache-Control": components["headers"]["PrivateCache"];
                [name: string]: unknown;
            };
            content: {
                "application/problem+json": components["schemas"]["ProblemDetails"];
            };
        };
        /** @description 503: DependencyUnavailable. Results or another temporary dependency is unavailable; no quota is consumed and no event is acknowledged as processed. */
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
        AppealId: components["schemas"]["OpaqueId"];
        /** @description Opaque client operation key. Same operation/payload replays its stored response; different payload conflicts. */
        IdempotencyKey: string;
        /** @description One strong Appeal version ETag; stale version returns 412 and is not automatically rebased. */
        IfMatch: string;
    };
    requestBodies: never;
    headers: {
        /** @description URL of the created/current Appeal resource. */
        AppealLocation: string;
        /** @description Strong validator for the Appeal aggregate version. */
        AppealETag: string;
        /** @description Strong validator for the local submission-window version. */
        WindowETag: string;
        /** @description Private representation/errors must not be stored by shared caches. */
        PrivateCache: "private, no-store";
    };
    pathItems: never;
}
export type $defs = Record<string, never>;
export interface operations {
    submitAppeal: {
        parameters: {
            query?: never;
            header: {
                /** @description Opaque client operation key. Same operation/payload replays its stored response; different payload conflicts. */
                "Idempotency-Key": components["parameters"]["IdempotencyKey"];
            };
            path?: never;
            cookie?: never;
        };
        requestBody: {
            content: {
                "application/json": components["schemas"]["SubmitAppealRequest"];
            };
        };
        responses: {
            /** @description Appeal registered */
            201: {
                headers: {
                    Location: components["headers"]["AppealLocation"];
                    ETag: components["headers"]["AppealETag"];
                    "Cache-Control": components["headers"]["PrivateCache"];
                    [name: string]: unknown;
                };
                content: {
                    "application/json": components["schemas"]["AppealView"];
                };
            };
            400: components["responses"]["InvalidRequest"];
            401: components["responses"]["Unauthorized"];
            403: components["responses"]["Forbidden"];
            409: components["responses"]["SubmitConflict"];
            503: components["responses"]["Unavailable"];
        };
    };
    getMyAppeals: {
        parameters: {
            query: {
                tournamentId: components["schemas"]["OpaqueId"];
            };
            header?: never;
            path?: never;
            cookie?: never;
        };
        requestBody?: never;
        responses: {
            /** @description Own appeal states */
            200: {
                headers: {
                    "Cache-Control": components["headers"]["PrivateCache"];
                    [name: string]: unknown;
                };
                content: {
                    "application/json": components["schemas"]["AppealList"];
                };
            };
            401: components["responses"]["Unauthorized"];
            403: components["responses"]["Forbidden"];
            503: components["responses"]["Unavailable"];
        };
    };
    getRegisteredAppeals: {
        parameters: {
            query?: {
                /** @description Optional explicit filter; only Registered is supported by this queue. */
                state?: "Registered";
            };
            header?: never;
            path: {
                tournamentId: components["parameters"]["TournamentId"];
            };
            cookie?: never;
        };
        requestBody?: never;
        responses: {
            /** @description Registered queue; each item contains only the minimum basis/status needed for a manual choice */
            200: {
                headers: {
                    "Cache-Control": components["headers"]["PrivateCache"];
                    [name: string]: unknown;
                };
                content: {
                    "application/json": components["schemas"]["AppealQueue"];
                };
            };
            401: components["responses"]["Unauthorized"];
            403: components["responses"]["Forbidden"];
            503: components["responses"]["Unavailable"];
        };
    };
    getAppeal: {
        parameters: {
            query?: never;
            header?: never;
            path: {
                appealId: components["parameters"]["AppealId"];
            };
            cookie?: never;
        };
        requestBody?: never;
        responses: {
            /** @description Current Appeal process view */
            200: {
                headers: {
                    ETag: components["headers"]["AppealETag"];
                    "Cache-Control": components["headers"]["PrivateCache"];
                    [name: string]: unknown;
                };
                content: {
                    "application/json": components["schemas"]["AppealView"];
                };
            };
            401: components["responses"]["Unauthorized"];
            403: components["responses"]["Forbidden"];
            404: components["responses"]["NotFound"];
            503: components["responses"]["Unavailable"];
        };
    };
    claimAppeal: {
        parameters: {
            query?: never;
            header: {
                /** @description Opaque client operation key. Same operation/payload replays its stored response; different payload conflicts. */
                "Idempotency-Key": components["parameters"]["IdempotencyKey"];
                /** @description One strong Appeal version ETag; stale version returns 412 and is not automatically rebased. */
                "If-Match": components["parameters"]["IfMatch"];
            };
            path: {
                appealId: components["parameters"]["AppealId"];
            };
            cookie?: never;
        };
        requestBody?: never;
        responses: {
            /** @description Selection attempt saved; Checking assignment is still pending */
            202: {
                headers: {
                    Location: components["headers"]["AppealLocation"];
                    ETag: components["headers"]["AppealETag"];
                    "Cache-Control": components["headers"]["PrivateCache"];
                    [name: string]: unknown;
                };
                content: {
                    "application/json": components["schemas"]["ClaimAccepted"];
                };
            };
            400: components["responses"]["InvalidRequest"];
            401: components["responses"]["Unauthorized"];
            403: components["responses"]["Forbidden"];
            404: components["responses"]["NotFound"];
            409: components["responses"]["ClaimConflict"];
            412: components["responses"]["VersionConflict"];
            428: components["responses"]["IfMatchRequired"];
            503: components["responses"]["Unavailable"];
        };
    };
    recordLocalTournamentEndFact: {
        parameters: {
            query?: never;
            header: {
                /** @description Opaque client operation key. Same operation/payload replays its stored response; different payload conflicts. */
                "Idempotency-Key": components["parameters"]["IdempotencyKey"];
            };
            path: {
                tournamentId: components["parameters"]["TournamentId"];
            };
            cookie?: never;
        };
        requestBody: {
            content: {
                "application/json": components["schemas"]["EndFactRequest"];
            };
        };
        responses: {
            /** @description Window closed or matching successful replay */
            200: {
                headers: {
                    ETag: components["headers"]["WindowETag"];
                    "Cache-Control": components["headers"]["PrivateCache"];
                    [name: string]: unknown;
                };
                content: {
                    "application/json": components["schemas"]["ClosedWindow"];
                };
            };
            400: components["responses"]["InvalidRequest"];
            401: components["responses"]["Unauthorized"];
            403: components["responses"]["Forbidden"];
            404: components["responses"]["NotFound"];
            409: components["responses"]["IdempotencyConflict"];
            503: components["responses"]["Unavailable"];
        };
    };
    getAppealsLiveness: {
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
    getAppealsReadiness: {
        parameters: {
            query?: never;
            header?: never;
            path?: never;
            cookie?: never;
        };
        requestBody?: never;
        responses: {
            /** @description Appeals database is ready */
            200: {
                headers: {
                    [name: string]: unknown;
                };
                content?: never;
            };
            /** @description Appeals database is unavailable */
            503: {
                headers: {
                    [name: string]: unknown;
                };
                content?: never;
            };
        };
    };
}
