export const STYLOBOT_HEADERS = {
    IS_BOT: 'x-stylobot-isbot',
    PROBABILITY: 'x-stylobot-probability',
    CONFIDENCE: 'x-stylobot-confidence',
    BOT_TYPE: 'x-stylobot-bottype',
    BOT_NAME: 'x-stylobot-botname',
    RISK_BAND: 'x-stylobot-riskband',
    ACTION: 'x-stylobot-action',
    THREAT_SCORE: 'x-stylobot-threatscore',
    THREAT_BAND: 'x-stylobot-threatband',
    POLICY: 'x-stylobot-policy',
    REQUEST_ID: 'x-stylobot-requestid',
};
export function parseStyloBotHeaders(headers) {
    const get = (key) => {
        const val = headers[key];
        if (Array.isArray(val))
            return val[0];
        return val ?? undefined;
    };
    const isBotRaw = get(STYLOBOT_HEADERS.IS_BOT);
    if (isBotRaw === undefined)
        return null;
    return {
        isBot: isBotRaw === 'true',
        botProbability: parseFloat(get(STYLOBOT_HEADERS.PROBABILITY) ?? '0'),
        confidence: parseFloat(get(STYLOBOT_HEADERS.CONFIDENCE) ?? '0'),
        botType: get(STYLOBOT_HEADERS.BOT_TYPE) || null,
        botName: get(STYLOBOT_HEADERS.BOT_NAME) || null,
        riskBand: get(STYLOBOT_HEADERS.RISK_BAND) ?? 'Unknown',
        recommendedAction: get(STYLOBOT_HEADERS.ACTION) ?? 'Allow',
        threatScore: parseFloat(get(STYLOBOT_HEADERS.THREAT_SCORE) ?? '0'),
        threatBand: get(STYLOBOT_HEADERS.THREAT_BAND) ?? 'None',
    };
}
//# sourceMappingURL=headers.js.map