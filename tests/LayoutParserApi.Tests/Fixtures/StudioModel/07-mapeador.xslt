<?xml version="1.0" encoding="utf-8"?>
<!-- SINTETICO. XSLT de exemplo contra o layout de saida 02 (pedido/numero/item/produto/qtd). Sem dado real. -->
<xsl:stylesheet version="1.0" xmlns:xsl="http://www.w3.org/1999/XSL/Transform">
  <xsl:output method="xml" indent="yes"/>
  <xsl:template match="/">
    <pedido>
      <numero><xsl:value-of select="ORDEM/CAB/NumeroPedido"/></numero>
      <xsl:for-each select="ORDEM/ITENS">
        <item>
          <produto><xsl:value-of select="Codigo"/></produto>
          <qtd>
            <xsl:choose>
              <xsl:when test="Quantidade &gt; 0"><xsl:value-of select="Quantidade"/></xsl:when>
              <xsl:otherwise>1</xsl:otherwise>
            </xsl:choose>
          </qtd>
        </item>
      </xsl:for-each>
    </pedido>
  </xsl:template>
</xsl:stylesheet>
