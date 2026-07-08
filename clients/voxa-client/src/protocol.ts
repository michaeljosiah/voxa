// AUTO-GENERATED from voxa-wire.schema.json (which is generated from the C# wire envelope
// records). Do not edit by hand. Regenerate: npm run generate

export const VOXA_WIRE_VERSION = 1;

export type SessionMessage = {
  type: "session";
  v: number;
  inputSampleRate: number;
  outputSampleRate: number;
};

export type TranscriptionMessage = {
  type: "transcription";
  text: string;
  isFinal: boolean;
  language?: string | null;
  speakerId?: string | null;
};

export type TextMessage = {
  type: "text";
  text: string;
};

export type ToolCallMessage = {
  type: "toolCall";
  callId: string;
  name: string;
  argumentsJson: string;
};

export type SpeakingMessage = {
  type: "speaking";
  who: "bot" | "user";
  started: boolean;
};

export type InterruptionMessage = {
  type: "interruption";
};

export type StatusMessage = {
  type: "status";
  message: string;
};

export type ErrorMessage = {
  type: "error";
  message: string;
};

export type EndMessage = {
  type: "end";
};

export type ClientEndMessage = {
  type: "end";
};

export type ClientTextMessage = {
  type: "text";
  text?: string | null;
};

export type ClientToolResultMessage = {
  type: "toolResult";
  callId?: string | null;
  resultJson?: string | null;
  isError?: boolean | null;
};

export type ServerMessage =
  | SessionMessage
  | TranscriptionMessage
  | TextMessage
  | ToolCallMessage
  | SpeakingMessage
  | InterruptionMessage
  | StatusMessage
  | ErrorMessage
  | EndMessage;

export type ClientMessage =
  | ClientEndMessage
  | ClientTextMessage
  | ClientToolResultMessage;
