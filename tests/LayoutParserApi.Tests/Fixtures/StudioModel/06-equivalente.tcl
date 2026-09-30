<!-- SINTETICO. Formato TCL do NOSSO motor (LayoutParserApi/Scripts/GenerateTclAndXsl.cs): descreve SO o layout TXT.
     O Sysmiddle nao usa TCL; isto e o que geramos a partir de 01-layout-entrada-txt.xml. -->
<MAP>
	<LINE identifier="CAB" name="LINHA_CAB">
		<FIELD name="TipoRegistro" length="3"/>
		<FIELD name="NumeroPedido" length="6"/>
		<CHILD>LINHA_ITEM</CHILD>
	</LINE>

	<LINE identifier="ITE" name="LINHA_ITEM">
		<FIELD name="TipoRegistro" length="3"/>
		<FIELD name="CodigoProduto" length="5"/>
		<FIELD name="Quantidade" length="4"/>
	</LINE>
</MAP>
