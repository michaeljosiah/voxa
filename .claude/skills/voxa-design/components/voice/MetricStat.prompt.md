Dashboard stat — uppercase micro-label over a big Cascadia Code value.

```jsx
const { MetricStat } = window.VOXA;
<MetricStat label="TTFB" value="412" unit="ms" delta="▼ 38 ms" deltaTone="good" />
```

- `deltaTone`: good (green), bad (red), flat (grey). Lower latency = good.
- Keep values pre-formatted; the component doesn't do math.
