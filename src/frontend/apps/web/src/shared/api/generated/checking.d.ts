// Generated from contracts/http/checking/openapi.json. Run npm run generate:api; do not edit.
export interface paths {
    "/api/v1/checking/queues": {
        parameters: {
            query?: never;
            header?: never;
            path?: never;
            cookie?: never;
        };
        /** @description Read-only queue metadata, ordered by createdAt then checkId. It does not reserve work or prove current availability. Senior queue requires Senior scope. Technical cursor pagination has no business quota. */
        get: operations["getCheckingQueue"];
        put?: never;
        post?: never;
        delete?: never;
        options?: never;
        head?: never;
        patch?: never;
        trace?: never;
    };
    "/api/v1/checking/me/assignments/next": {
        parameters: {
            query?: never;
            header?: never;
            path?: never;
            cookie?: never;
        };
        get?: never;
        put?: never;
        /** @description Atomically assign the next available Check to the authenticated subject. Reviewer identity is not a body field. Shared occupancy across all tournaments/queues/rechecks. No If-Match: server selects the Check. Empty queue is 204; existing occupancy is 409 ReviewerBusy. A replay preserves the original outcome even if the queue/current state has changed. */
        post: operations["takeNextCheck"];
        delete?: never;
        options?: never;
        head?: never;
        patch?: never;
        trace?: never;
    };
    "/api/v1/checking/me/assignment": {
        parameters: {
            query?: never;
            header?: never;
            path?: never;
            cookie?: never;
        };
        /** @description Current subject assignment across all tournaments. No filter may hide an existing assignment. Closing the page, disconnecting, or waiting does not release it. */
        get: operations["getMyAssignment"];
        put?: never;
        post?: never;
        delete?: never;
        options?: never;
        head?: never;
        patch?: never;
        trace?: never;
    };
    "/api/v1/checking/checks/{checkId}": {
        parameters: {
            query?: never;
            header?: never;
            path?: never;
            cookie?: never;
        };
        /** @description Minimal Check snapshot for the owner or authorized jury scope; never includes capability, materialRef, or public history. The current owner obtains capability from GET /me/assignment. */
        get: operations["getCheck"];
        put?: never;
        post?: never;
        delete?: never;
        options?: never;
        head?: never;
        patch?: never;
        trace?: never;
    };
    "/api/v1/checking/checks/{checkId}/material": {
        parameters: {
            query?: never;
            header?: never;
            path?: never;
            cookie?: never;
        };
        /** @description Read only the prepared material for the authenticated current assignment owner. No user path/URL input. Missing/unavailable material is an observable read failure, never Incorrect. Responses are private/no-store. */
        get: operations["getCheckMaterial"];
        put?: never;
        post?: never;
        delete?: never;
        options?: never;
        head?: never;
        patch?: never;
        trace?: never;
    };
    "/api/v1/checking/checks/{checkId}/complete": {
        parameters: {
            query?: never;
            header?: never;
            path?: never;
            cookie?: never;
        };
        get?: never;
        put?: never;
        /** @description Save binary verdict, close assignment and persist outbox atomically. The reviewer is free after commit; this response does not promise Results has processed/published it. SeniorAssigned completion uses SeniorEscalation. */
        post: operations["completeCheck"];
        delete?: never;
        options?: never;
        head?: never;
        patch?: never;
        trace?: never;
    };
    "/api/v1/checking/checks/{checkId}/refuse": {
        parameters: {
            query?: never;
            header?: never;
            path?: never;
            cookie?: never;
        };
        get?: never;
        put?: never;
        /** @description Refuse only Assigned work: atomically close assignment, revoke capability, release reviewer and enqueue the SAME Check as SeniorQueued. This also applies to an appeal Check. SeniorAssigned cannot refuse again; no automatic release/return-to-general route. */
        post: operations["refuseCheck"];
        delete?: never;
        options?: never;
        head?: never;
        patch?: never;
        trace?: never;
    };
    "/dev/v1/session/select": {
        parameters: {
            query?: never;
            header?: never;
            path?: never;
            cookie?: never;
        };
        get?: never;
        put?: never;
        /** @description Local/test-only stateless credential issuance, not a business state mutation. Disabled (404) outside Local/Test. The server resolves testSubjectId from fixtures and selects stable subject/roles/team/tournament scope; no browser role/team/scope fields. Signed credential verified by every API; lifetime is configured by the issuer, not fixed by this contract. User credentials cannot replace service credentials. This adapter is replaced by real identity later without changing internal subjectId. */
        post: operations["selectDevSession"];
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
        get: operations["getLiveness"];
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
        get: operations["getReadiness"];
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
        /** @description Opaque identifier. Do not derive rights or ordering from its value. */
        OpaqueId: string;
        /** @description Exact decimal version, compared as an integer, never a JavaScript Number. */
        Version: string;
        /** @description Exact decimal version, compared as an integer, never a JavaScript Number. */
        PositiveVersion: string;
        /**
         * Format: date-time
         * @description UTC; not an ordering guarantee.
         */
        UtcTimestamp: string;
        /** @enum {string} */
        Outcome: "Correct" | "Incorrect";
        /** @enum {string} */
        Kind: "Initial" | "Appeal";
        /** @enum {string} */
        DecisionOrigin: "OrdinaryReview" | "AppealReview" | "SeniorEscalation";
        /** @enum {string} */
        Queue: "general" | "senior";
        /** @example "3" */
        ETag: string;
        CheckView: {
            checkId: components["schemas"]["OpaqueId"];
            solutionId: components["schemas"]["OpaqueId"];
            materialVersionId: components["schemas"]["OpaqueId"];
            tournamentId: components["schemas"]["OpaqueId"];
            teamId: components["schemas"]["OpaqueId"];
            taskId: components["schemas"]["OpaqueId"];
            kind: components["schemas"]["Kind"];
            /** @enum {string} */
            state: "Queued" | "Assigned" | "SeniorQueued" | "SeniorAssigned" | "Completed";
            version: components["schemas"]["PositiveVersion"];
            reviewRevision: components["schemas"]["PositiveVersion"];
            createdAt: components["schemas"]["UtcTimestamp"];
            outcome?: components["schemas"]["Outcome"];
            decisionOrigin?: components["schemas"]["DecisionOrigin"];
        } & (unknown & unknown);
        AssignedCheckView: components["schemas"]["CheckView"] & {
            /** @enum {unknown} */
            state?: "Assigned" | "SeniorAssigned";
        };
        CompletedCheckView: components["schemas"]["CheckView"] & {
            /** @constant */
            state?: "Completed";
        };
        SeniorQueuedCheckView: components["schemas"]["CheckView"] & {
            /** @constant */
            state?: "SeniorQueued";
        };
        MyAssignment: {
            check: components["schemas"]["AssignedCheckView"];
            assignmentId: components["schemas"]["OpaqueId"];
            reviewerId: components["schemas"]["OpaqueId"];
            startedAt: components["schemas"]["UtcTimestamp"];
            /** @description Secret capability; returned only to the authenticated current owner. Never log or send to RabbitMQ. */
            capability: string;
        };
        TakeNextRequest: {
            tournamentId: components["schemas"]["OpaqueId"];
            queue: components["schemas"]["Queue"];
        };
        CompleteRequest: {
            assignmentId: components["schemas"]["OpaqueId"];
            capability: string;
            outcome: components["schemas"]["Outcome"];
        };
        RefuseRequest: {
            assignmentId: components["schemas"]["OpaqueId"];
            capability: string;
        };
        QueueItem: {
            checkId: components["schemas"]["OpaqueId"];
            solutionId: components["schemas"]["OpaqueId"];
            tournamentId: components["schemas"]["OpaqueId"];
            teamId: components["schemas"]["OpaqueId"];
            taskId: components["schemas"]["OpaqueId"];
            kind: components["schemas"]["Kind"];
            /** @enum {string} */
            state: "Queued" | "SeniorQueued";
            createdAt: components["schemas"]["UtcTimestamp"];
        };
        QueuePage: {
            tournamentId: components["schemas"]["OpaqueId"];
            queue: components["schemas"]["Queue"];
            items: components["schemas"]["QueueItem"][];
            nextCursor: string | null;
        };
        SessionSelectRequest: {
            /** @description Server-side fixture key. Roles/team/scope cannot be supplied by the browser. */
            testSubjectId: string;
        };
        DevProfile: {
            subjectId: components["schemas"]["OpaqueId"];
            displayName: string;
            roles: ("Jury" | "Senior" | "Team" | "Organizer")[];
            tournamentIds: components["schemas"]["OpaqueId"][];
            teamId?: components["schemas"]["OpaqueId"];
        } & unknown;
        DevSession: {
            accessToken: string;
            /** @constant */
            tokenType: "Bearer";
            expiresAt: components["schemas"]["UtcTimestamp"];
            profile: components["schemas"]["DevProfile"];
        };
        ProblemDetails: {
            /** Format: uri-reference */
            type: string;
            title: string;
            status: number;
            detail?: string;
            /** Format: uri-reference */
            instance?: string;
            code: string;
            correlationId: components["schemas"]["OpaqueId"];
        };
        Liveness: {
            /** @constant */
            status: "Alive";
        };
        Readiness: {
            /** @enum {unknown} */
            status: "Ready" | "Unavailable";
            database: boolean;
            broker: boolean;
        };
    };
    responses: {
        /** @description 400: InvalidRequest, IdempotencyKeyRequired. No material, tokens, stack traces, or private request data. */
        BadRequest: {
            headers: {
                "Cache-Control": components["headers"]["PrivateCache"];
                [name: string]: unknown;
            };
            content: {
                "application/problem+json": components["schemas"]["ProblemDetails"];
            };
        };
        /** @description 401: AuthenticationRequired. No material, tokens, stack traces, or private request data. */
        Unauthorized: {
            headers: {
                "Cache-Control": components["headers"]["PrivateCache"];
                [name: string]: unknown;
            };
            content: {
                "application/problem+json": components["schemas"]["ProblemDetails"];
            };
        };
        /** @description 403: Forbidden. No material, tokens, stack traces, or private request data. */
        Forbidden: {
            headers: {
                "Cache-Control": components["headers"]["PrivateCache"];
                [name: string]: unknown;
            };
            content: {
                "application/problem+json": components["schemas"]["ProblemDetails"];
            };
        };
        /** @description 404: CheckNotFound. No material, tokens, stack traces, or private request data. */
        NotFound: {
            headers: {
                "Cache-Control": components["headers"]["PrivateCache"];
                [name: string]: unknown;
            };
            content: {
                "application/problem+json": components["schemas"]["ProblemDetails"];
            };
        };
        /** @description 409: ReviewerBusy, CheckStateConflict, AssignmentConflict, IdempotencyConflict. No material, tokens, stack traces, or private request data. */
        Conflict: {
            headers: {
                "Cache-Control": components["headers"]["PrivateCache"];
                [name: string]: unknown;
            };
            content: {
                "application/problem+json": components["schemas"]["ProblemDetails"];
            };
        };
        /** @description 412: CheckVersionConflict. No material, tokens, stack traces, or private request data. */
        PreconditionFailed: {
            headers: {
                "Cache-Control": components["headers"]["PrivateCache"];
                [name: string]: unknown;
            };
            content: {
                "application/problem+json": components["schemas"]["ProblemDetails"];
            };
        };
        /** @description 428: IfMatchRequired. No material, tokens, stack traces, or private request data. */
        PreconditionRequired: {
            headers: {
                "Cache-Control": components["headers"]["PrivateCache"];
                [name: string]: unknown;
            };
            content: {
                "application/problem+json": components["schemas"]["ProblemDetails"];
            };
        };
        /** @description 503: DependencyUnavailable, MaterialUnavailable. No material, tokens, stack traces, or private request data. */
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
        CheckId: components["schemas"]["OpaqueId"];
        /** @description Scope: authenticated subjectId + operation (including target checkId) + key. Persist normalized request hash and original response. Authenticate/authorize first; replay a successful command before evaluating If-Match/current state. Different payload with same key: 409 IdempotencyConflict. */
        IdempotencyKey: string;
        /** @description The single strong Check ETag returned by GET. Missing: 428; stale: 412. Successful replay is checked first. Wildcards/multiple tags are not a substitute for the expected version. */
        IfMatch: components["schemas"]["ETag"];
    };
    requestBodies: never;
    headers: {
        /** @description All private data, including capabilities, is non-cacheable. */
        PrivateCache: "private, no-store";
        /** @description Strong ETag of the Check version. A successful idempotent replay preserves the original ETag. */
        ETag: components["schemas"]["ETag"];
    };
    pathItems: never;
}
export type $defs = Record<string, never>;
export interface operations {
    getCheckingQueue: {
        parameters: {
            query: {
                tournamentId: components["schemas"]["OpaqueId"];
                queue: components["schemas"]["Queue"];
                cursor?: string;
            };
            header?: never;
            path?: never;
            cookie?: never;
        };
        requestBody?: never;
        responses: {
            /** @description Visible queue page */
            200: {
                headers: {
                    "Cache-Control": components["headers"]["PrivateCache"];
                    [name: string]: unknown;
                };
                content: {
                    "application/json": components["schemas"]["QueuePage"];
                };
            };
            400: components["responses"]["BadRequest"];
            401: components["responses"]["Unauthorized"];
            403: components["responses"]["Forbidden"];
            503: components["responses"]["Unavailable"];
        };
    };
    takeNextCheck: {
        parameters: {
            query?: never;
            header: {
                /** @description Scope: authenticated subjectId + operation (including target checkId) + key. Persist normalized request hash and original response. Authenticate/authorize first; replay a successful command before evaluating If-Match/current state. Different payload with same key: 409 IdempotencyConflict. */
                "Idempotency-Key": components["parameters"]["IdempotencyKey"];
            };
            path?: never;
            cookie?: never;
        };
        requestBody: {
            content: {
                "application/json": components["schemas"]["TakeNextRequest"];
            };
        };
        responses: {
            /** @description Committed assignment, before Results processing */
            200: {
                headers: {
                    "Cache-Control": components["headers"]["PrivateCache"];
                    ETag: components["headers"]["ETag"];
                    [name: string]: unknown;
                };
                content: {
                    "application/json": components["schemas"]["MyAssignment"];
                };
            };
            /** @description No available work; no response body. */
            204: {
                headers: {
                    "Cache-Control": components["headers"]["PrivateCache"];
                    [name: string]: unknown;
                };
                content?: never;
            };
            400: components["responses"]["BadRequest"];
            401: components["responses"]["Unauthorized"];
            403: components["responses"]["Forbidden"];
            409: components["responses"]["Conflict"];
            503: components["responses"]["Unavailable"];
        };
    };
    getMyAssignment: {
        parameters: {
            query?: never;
            header?: never;
            path?: never;
            cookie?: never;
        };
        requestBody?: never;
        responses: {
            /** @description Current assignment and owner-only capability */
            200: {
                headers: {
                    "Cache-Control": components["headers"]["PrivateCache"];
                    ETag: components["headers"]["ETag"];
                    [name: string]: unknown;
                };
                content: {
                    "application/json": components["schemas"]["MyAssignment"];
                };
            };
            /** @description Subject has no active assignment; no body. */
            204: {
                headers: {
                    "Cache-Control": components["headers"]["PrivateCache"];
                    [name: string]: unknown;
                };
                content?: never;
            };
            401: components["responses"]["Unauthorized"];
            403: components["responses"]["Forbidden"];
            503: components["responses"]["Unavailable"];
        };
    };
    getCheck: {
        parameters: {
            query?: never;
            header?: never;
            path: {
                checkId: components["parameters"]["CheckId"];
            };
            cookie?: never;
        };
        requestBody?: never;
        responses: {
            /** @description Check snapshot */
            200: {
                headers: {
                    "Cache-Control": components["headers"]["PrivateCache"];
                    ETag: components["headers"]["ETag"];
                    [name: string]: unknown;
                };
                content: {
                    "application/json": components["schemas"]["CheckView"];
                };
            };
            400: components["responses"]["BadRequest"];
            401: components["responses"]["Unauthorized"];
            403: components["responses"]["Forbidden"];
            404: components["responses"]["NotFound"];
            503: components["responses"]["Unavailable"];
        };
    };
    getCheckMaterial: {
        parameters: {
            query?: never;
            header?: never;
            path: {
                checkId: components["parameters"]["CheckId"];
            };
            cookie?: never;
        };
        requestBody?: never;
        responses: {
            /** @description Prepared material bytes; not JSON and not a public material URL. */
            200: {
                headers: {
                    "Cache-Control": components["headers"]["PrivateCache"];
                    [name: string]: unknown;
                };
                content: {
                    "text/plain": string;
                };
            };
            400: components["responses"]["BadRequest"];
            401: components["responses"]["Unauthorized"];
            403: components["responses"]["Forbidden"];
            404: components["responses"]["NotFound"];
            503: components["responses"]["Unavailable"];
        };
    };
    completeCheck: {
        parameters: {
            query?: never;
            header: {
                /** @description Scope: authenticated subjectId + operation (including target checkId) + key. Persist normalized request hash and original response. Authenticate/authorize first; replay a successful command before evaluating If-Match/current state. Different payload with same key: 409 IdempotencyConflict. */
                "Idempotency-Key": components["parameters"]["IdempotencyKey"];
                /** @description The single strong Check ETag returned by GET. Missing: 428; stale: 412. Successful replay is checked first. Wildcards/multiple tags are not a substitute for the expected version. */
                "If-Match": components["parameters"]["IfMatch"];
            };
            path: {
                checkId: components["parameters"]["CheckId"];
            };
            cookie?: never;
        };
        requestBody: {
            content: {
                "application/json": components["schemas"]["CompleteRequest"];
            };
        };
        responses: {
            /** @description Committed Check transition */
            200: {
                headers: {
                    "Cache-Control": components["headers"]["PrivateCache"];
                    ETag: components["headers"]["ETag"];
                    [name: string]: unknown;
                };
                content: {
                    "application/json": components["schemas"]["CompletedCheckView"];
                };
            };
            400: components["responses"]["BadRequest"];
            401: components["responses"]["Unauthorized"];
            403: components["responses"]["Forbidden"];
            404: components["responses"]["NotFound"];
            409: components["responses"]["Conflict"];
            412: components["responses"]["PreconditionFailed"];
            428: components["responses"]["PreconditionRequired"];
            503: components["responses"]["Unavailable"];
        };
    };
    refuseCheck: {
        parameters: {
            query?: never;
            header: {
                /** @description Scope: authenticated subjectId + operation (including target checkId) + key. Persist normalized request hash and original response. Authenticate/authorize first; replay a successful command before evaluating If-Match/current state. Different payload with same key: 409 IdempotencyConflict. */
                "Idempotency-Key": components["parameters"]["IdempotencyKey"];
                /** @description The single strong Check ETag returned by GET. Missing: 428; stale: 412. Successful replay is checked first. Wildcards/multiple tags are not a substitute for the expected version. */
                "If-Match": components["parameters"]["IfMatch"];
            };
            path: {
                checkId: components["parameters"]["CheckId"];
            };
            cookie?: never;
        };
        requestBody: {
            content: {
                "application/json": components["schemas"]["RefuseRequest"];
            };
        };
        responses: {
            /** @description Committed Check transition */
            200: {
                headers: {
                    "Cache-Control": components["headers"]["PrivateCache"];
                    ETag: components["headers"]["ETag"];
                    [name: string]: unknown;
                };
                content: {
                    "application/json": components["schemas"]["SeniorQueuedCheckView"];
                };
            };
            400: components["responses"]["BadRequest"];
            401: components["responses"]["Unauthorized"];
            403: components["responses"]["Forbidden"];
            404: components["responses"]["NotFound"];
            409: components["responses"]["Conflict"];
            412: components["responses"]["PreconditionFailed"];
            428: components["responses"]["PreconditionRequired"];
            503: components["responses"]["Unavailable"];
        };
    };
    selectDevSession: {
        parameters: {
            query?: never;
            header?: never;
            path?: never;
            cookie?: never;
        };
        requestBody: {
            content: {
                "application/json": components["schemas"]["SessionSelectRequest"];
            };
        };
        responses: {
            /** @description Signed credential and server-approved profile */
            200: {
                headers: {
                    "Cache-Control": components["headers"]["PrivateCache"];
                    [name: string]: unknown;
                };
                content: {
                    "application/json": components["schemas"]["DevSession"];
                };
            };
            400: components["responses"]["BadRequest"];
            403: components["responses"]["Forbidden"];
            /** @description Dev route is disabled, or the fixture subject is unavailable. */
            404: {
                headers: {
                    [name: string]: unknown;
                };
                content: {
                    "application/problem+json": components["schemas"]["ProblemDetails"];
                };
            };
            503: components["responses"]["Unavailable"];
        };
    };
    getLiveness: {
        parameters: {
            query?: never;
            header?: never;
            path?: never;
            cookie?: never;
        };
        requestBody?: never;
        responses: {
            /** @description Process alive */
            200: {
                headers: {
                    [name: string]: unknown;
                };
                content: {
                    "application/json": components["schemas"]["Liveness"];
                };
            };
        };
    };
    getReadiness: {
        parameters: {
            query?: never;
            header?: never;
            path?: never;
            cookie?: never;
        };
        requestBody?: never;
        responses: {
            /** @description Database and broker available */
            200: {
                headers: {
                    [name: string]: unknown;
                };
                content: {
                    "application/json": components["schemas"]["Readiness"];
                };
            };
            /** @description Database or broker unavailable; not a ProblemDetails business response */
            503: {
                headers: {
                    [name: string]: unknown;
                };
                content: {
                    "application/json": components["schemas"]["Readiness"];
                };
            };
        };
    };
}
