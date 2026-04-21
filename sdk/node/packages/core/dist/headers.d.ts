import type { Verdict } from './types.js';
export declare const STYLOBOT_HEADERS: {
    readonly IS_BOT: "x-stylobot-isbot";
    readonly PROBABILITY: "x-stylobot-probability";
    readonly CONFIDENCE: "x-stylobot-confidence";
    readonly BOT_TYPE: "x-stylobot-bottype";
    readonly BOT_NAME: "x-stylobot-botname";
    readonly RISK_BAND: "x-stylobot-riskband";
    readonly ACTION: "x-stylobot-action";
    readonly THREAT_SCORE: "x-stylobot-threatscore";
    readonly THREAT_BAND: "x-stylobot-threatband";
    readonly POLICY: "x-stylobot-policy";
    readonly REQUEST_ID: "x-stylobot-requestid";
};
export declare function parseStyloBotHeaders(headers: Record<string, string | string[] | undefined>): Verdict | null;
//# sourceMappingURL=headers.d.ts.map