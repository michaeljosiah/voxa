// Minimal typed emitter — the package has zero runtime dependencies, so no eventemitter3 etc.
export type Handler = (...args: never[]) => void;

export class Emitter<Events extends Record<string, Handler>> {
  private readonly handlers = new Map<keyof Events, Set<Events[keyof Events]>>();

  /** Subscribe; returns an unsubscribe function. */
  on<K extends keyof Events>(event: K, handler: Events[K]): () => void {
    let set = this.handlers.get(event);
    if (!set) {
      set = new Set();
      this.handlers.set(event, set);
    }
    set.add(handler);
    return () => set.delete(handler);
  }

  protected emit<K extends keyof Events>(event: K, ...args: Parameters<Events[K]>): void {
    const set = this.handlers.get(event);
    if (!set) return;
    for (const handler of set) (handler as (...a: Parameters<Events[K]>) => void)(...args);
  }
}
