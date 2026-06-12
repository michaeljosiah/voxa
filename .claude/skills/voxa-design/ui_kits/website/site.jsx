function Site() {
  React.useEffect(() => { window.lucide && lucide.createIcons({ attrs: { 'stroke-width': 1.75 } }); });
  return (
    <div>
      <SiteNav />
      <Hero />
      <Features />
      <Footer />
    </div>
  );
}

ReactDOM.createRoot(document.getElementById('root')).render(<Site />);
