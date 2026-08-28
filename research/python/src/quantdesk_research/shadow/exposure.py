class ExposureCalculator:
    def calculate_exposure(
        self, holdings: dict[str, float], prices: dict[str, float]
    ) -> dict[str, float]:
        """
        Independently calculate economic exposure.
        holdings: symbol -> quantity
        prices: symbol -> current_price
        """
        exposures = {}
        for symbol, quantity in holdings.items():
            price = prices.get(symbol, 0.0)
            exposures[symbol] = quantity * price
        return exposures

    def calculate_portfolio_risk(self, exposures: dict[str, float]) -> dict[str, float]:
        """
        Simple risk metrics for shadow auditing.
        """
        total_exposure = sum(abs(v) for v in exposures.values())
        net_exposure = sum(v for v in exposures.values())

        return {"total_exposure": total_exposure, "net_exposure": net_exposure}
